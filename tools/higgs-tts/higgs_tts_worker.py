"""Warm local Higgs Audio v3 TTS worker for Speak Audio Studio.

Serves /say and /clone on a loopback port, or runs a one-shot CLI pass.
Security contract mirrors the Qwen3 TTS worker (worker_security module).

One-shot CLI:
  higgs_tts_worker.py say --text "..." --out out.wav --model <dir> [--device auto]
  higgs_tts_worker.py clone --text "..." --ref-audio ref.wav --out out.wav --model <dir> [--device auto]
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import threading
import time
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
for _p in (str(HERE), str(TOOLS)):
    if _p not in sys.path:
        sys.path.insert(0, _p)

import numpy as np
import soundfile as sf
import torch
import torchaudio
from fastapi import FastAPI, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
import uvicorn

import native
from native import (
    HiggsAudioCodec,
    SAMPLE_RATE,
    apply_delay_pattern,
    build_native_model,
    load_native_weights,
    load_tokenizer,
    read_config,
    reverse_delay_pattern,
    trim_silence_edges,
)
from worker_security import (
    RequestRejected,
    checked_content_length,
    ensure_loopback_host,
    is_authorized,
    is_loopback_host_header,
    public_error_code,
    require_json_content_type,
    resolve_auth_token,
    validate_file,
    validate_output_wav,
)

MAX_REQUEST_BYTES = 256 * 1024
MAX_TEXT_CHARS = 20_000
MAX_PATH_CHARS = 32_767
MAX_KEEP_ALIVE_MINUTES = 24 * 60
MAX_REFERENCE_SECONDS = 100.0
DEFAULT_MAX_NEW_TOKENS = 2048
DEFAULT_TEMPERATURE = 0.8
DEFAULT_TOP_P = 0.95
DEFAULT_TOP_K = 50


def emit(event: str, **fields: Any) -> None:
    payload: dict[str, Any] = {"event": event}
    payload.update(fields)
    print(json.dumps(payload), flush=True)


class WorkerRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True, str_strip_whitespace=True)


class SayRequest(WorkerRequest):
    text: str = Field(min_length=1, max_length=MAX_TEXT_CHARS)
    output: str = Field(min_length=1, max_length=MAX_PATH_CHARS)
    language: str = Field(default="Auto", max_length=64)


class CloneRequest(WorkerRequest):
    text: str = Field(min_length=1, max_length=MAX_TEXT_CHARS)
    output: str = Field(min_length=1, max_length=MAX_PATH_CHARS)
    ref_audio: str = Field(min_length=1, max_length=MAX_PATH_CHARS)
    language: str = Field(default="Auto", max_length=64)
    ref_text: str = Field(default="", max_length=MAX_TEXT_CHARS)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Warm local Higgs Audio v3 TTS worker.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8769)
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--startup-timeout-seconds", type=int, default=600)
    parser.add_argument("--idle-minutes", type=float, default=10)
    parser.add_argument("--max-reference-audio-megabytes", type=int, default=512)
    parser.add_argument("--auth-token", default=None, help=argparse.SUPPRESS)
    return parser.parse_args()


def security_response(status: int, message: str) -> JSONResponse:
    return JSONResponse(
        status_code=status,
        content={"error": message},
        headers={"Cache-Control": "no-store"},
    )


def _normalize_peak(wav: "any", target: float = 0.95) -> "any":
    if torch.is_tensor(wav):
        peak = wav.abs().max().item() if wav.numel() else 0.0
        if peak > 1e-6 and peak < target:
            return (wav * (target / peak)).clamp(-1.0, 1.0)
        return wav
    arr = np.asarray(wav)
    peak = float(np.abs(arr).max()) if arr.size else 0.0
    if peak > 1e-6 and peak < target:
        return (arr * (target / peak)).clip(-1.0, 1.0)
    return arr


def _is_cjk(char: str) -> bool:
    cp = ord(char)
    return (
        0x4E00 <= cp <= 0x9FFF
        or 0x3400 <= cp <= 0x4DBF
        or 0x20000 <= cp <= 0x2A6DF
        or 0x3040 <= cp <= 0x30FF
        or 0x30A0 <= cp <= 0x30FF
        or 0xAC00 <= cp <= 0xD7AF
        or 0x0E00 <= cp <= 0x0E7F
        or 0x1000 <= cp <= 0x109F
        or 0x1780 <= cp <= 0x17FF
    )


def _tag_safe_boundary(segment: str) -> int | None:
    import re

    boundary = re.compile(
        r"(<\|prosody:(?:pause|long_pause)\|>|[.!?]+(?:\s|$)|[。？！\u0964\u0965\u061F\u104B\u0F0D]+)"
    )
    tag_ranges = [(m.start(), m.end()) for m in re.finditer(r"<\|[^|]*\|>", segment)]
    last_end = None
    for match in boundary.finditer(segment):
        end = match.end()
        if any(start < end < stop for start, stop in tag_ranges):
            continue
        last_end = end
    return last_end


def _smart_chunk_text(text: str, words_per_chunk: int = 45) -> list[str]:
    text = text.strip()
    if not text:
        return []
    cjk_count = sum(1 for ch in text if _is_cjk(ch))
    alpha_count = sum(1 for ch in text if ch.isalpha() or _is_cjk(ch))
    words = text.split()
    if len(words) <= words_per_chunk:
        return [text]
    chunks: list[str] = []
    current: list[str] = []
    for word in words:
        current.append(word)
        if len(current) >= words_per_chunk:
            candidate = " ".join(current)
            split = _tag_safe_boundary(candidate)
            if split is not None and split >= max(20, len(candidate) // 3):
                final = candidate[:split].strip()
                rest = candidate[split:].strip()
                if final:
                    chunks.append(final)
                current = rest.split() if rest else []
            else:
                chunks.append(candidate.strip())
                current = []
    if current:
        chunks.append(" ".join(current).strip())
    return [chunk for chunk in chunks if chunk]


MIN_GPU_FREE_BYTES = 10 * 1024**3


def _resolve_device(device_name: str) -> torch.device:
    if device_name == "auto":
        if torch.cuda.is_available():
            free, _total = torch.cuda.mem_get_info()
            if free >= MIN_GPU_FREE_BYTES:
                return torch.device("cuda")
            print(
                f"GPU detected but only {free / 1024**3:.1f} GB free; "
                f"model needs ~{MIN_GPU_FREE_BYTES / 1024**3:.0f} GB. Falling back to CPU.",
                file=sys.stderr,
                flush=True,
            )
        return torch.device("cpu")
    device = torch.device(device_name)
    if device.type == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA was selected, but torch.cuda is not available.")
    return device


class HiggsTtsBundle:
    def __init__(self, model_dir: Path, device: torch.device):
        self.model_dir = model_dir
        self.device = device
        self.dtype = torch.bfloat16
        config = read_config(model_dir)
        self.model = build_native_model(config, self.dtype, "sdpa")
        load_native_weights(self.model, model_dir, device, self.dtype)
        self.codec = HiggsAudioCodec.from_pretrained(
            model_dir, device=device, dtype=torch.float32, runtime_device=device
        )
        self.tokenizer = load_tokenizer(model_dir)

    @torch.no_grad()
    def _encode_reference(self, wav: torch.Tensor, sample_rate: int) -> torch.Tensor | None:
        wav = trim_silence_edges(wav, sample_rate, -42.0)
        max_samples = int(MAX_REFERENCE_SECONDS * sample_rate)
        if max_samples > 0 and wav.numel() > max_samples:
            wav = wav[:max_samples].contiguous()
        if wav.numel() == 0:
            return None
        codec = self.codec
        cast_back = codec.dtype != torch.float32
        if cast_back:
            codec.model = codec.model.to(dtype=torch.float32)
            codec.dtype = torch.float32
        try:
            raw = codec.encode_reference(wav, sample_rate)
        finally:
            if cast_back:
                codec.model = codec.model.to(dtype=self.dtype)
                codec.dtype = self.dtype
        return apply_delay_pattern(raw)

    @torch.no_grad()
    def synthesize(self, text: str, ref_wav: torch.Tensor | None, ref_sr: int | None, ref_text: str) -> torch.Tensor:
        ref_delayed = None
        if ref_wav is not None and ref_sr is not None:
            ref_delayed = self._encode_reference(ref_wav, ref_sr)
        prompt_ids = self.tokenizer.build_prompt(
            text,
            num_ref_tokens=0 if ref_delayed is None else int(ref_delayed.shape[0]),
            reference_text=ref_text.strip() or None,
        )
        with torch.inference_mode():
            delayed = self.model.generate_codes(
                prompt_ids,
                ref_delayed,
                max_new_tokens=DEFAULT_MAX_NEW_TOKENS,
                temperature=DEFAULT_TEMPERATURE,
                top_p=DEFAULT_TOP_P,
                top_k=DEFAULT_TOP_K,
            )
            raw = reverse_delay_pattern(delayed)
            audio = self.codec.decode(raw)
        if not torch.isfinite(audio).all():
            raise RuntimeError("Higgs generated non-finite audio samples.")
        return audio

    def synthesize_chunked(self, text: str, ref_wav: torch.Tensor | None, ref_sr: int | None, ref_text: str) -> torch.Tensor:
        chunks = _smart_chunk_text(text)
        if not chunks:
            raise ValueError("Text cannot be empty.")
        segments: list[torch.Tensor] = []
        for index, chunk in enumerate(chunks):
            segment = self.synthesize(chunk, ref_wav, ref_sr, ref_text)
            segments.append(segment)
            print(f"Higgs chunk {index + 1}/{len(chunks)} done ({segment.numel() / SAMPLE_RATE:.1f}s)", flush=True)
        if len(segments) == 1:
            return segments[0]
        silence = torch.zeros(int(0.15 * SAMPLE_RATE), dtype=torch.float32)
        parts: list[torch.Tensor] = []
        for segment in segments:
            if parts:
                parts.append(silence)
            parts.append(segment)
        return torch.cat(parts, dim=-1).contiguous()

    def write_wav(self, audio: torch.Tensor, output_path: Path) -> None:
        audio = _normalize_peak(audio.detach().float().cpu(), 0.95)
        tmp_path = output_path.with_name(output_path.stem + ".tmp.wav")
        sf.write(str(tmp_path), audio.numpy(), SAMPLE_RATE, format="WAV", subtype="PCM_16")
        os.replace(str(tmp_path), str(output_path))


def load_reference_audio(path: Path) -> tuple[torch.Tensor, int]:
    try:
        wav_np, sample_rate = sf.read(str(path), dtype="float32", always_2d=True)
        wav = torch.from_numpy(wav_np.T).float()
    except Exception:
        wav, sample_rate = torchaudio.load(str(path))
    if wav.ndim == 2 and wav.shape[0] > 1:
        wav = wav.mean(dim=0)
    else:
        wav = wav.squeeze(0)
    wav = wav.detach().float().cpu().contiguous()
    wav = _normalize_peak(wav, 0.9)
    return wav, int(sample_rate)


def main() -> int:
    args = parse_args()
    try:
        host = ensure_loopback_host(args.host)
        auth_token = resolve_auth_token(args.auth_token)
        model_dir = Path(args.model)
        if not (model_dir / "model.safetensors").is_file():
            raise RuntimeError(f"Model weights not found: {model_dir / 'model.safetensors'}")

        device = _resolve_device(args.device)
        print(f"Loading Higgs Audio v3 from {model_dir} on {device} (bf16)...", flush=True)
        started = time.monotonic()
        bundle = HiggsTtsBundle(model_dir, device)
        print(f"Model ready in {time.monotonic() - started:.1f}s", flush=True)

        app = FastAPI(title="Higgs Audio v3 TTS Worker", version="1.0.0")
        activity_lock = threading.Lock()
        last_activity = time.monotonic()
        active_requests = 0
        stopping = False
        idle_minutes = float(args.idle_minutes) if args.idle_minutes > 0 else 10.0

        def begin_activity() -> None:
            nonlocal active_requests, last_activity
            with activity_lock:
                active_requests += 1
                last_activity = time.monotonic()

        def end_activity() -> None:
            nonlocal active_requests, last_activity
            with activity_lock:
                active_requests = max(0, active_requests - 1)
                last_activity = time.monotonic()

        def request_shutdown(reason: str, *, require_idle: bool = False) -> bool:
            nonlocal stopping
            with activity_lock:
                if stopping or (require_idle and active_requests > 0):
                    return False
                stopping = True

            def stop_when_inference_finishes() -> None:
                time.sleep(0.25)
                emit("stopping", reason=reason)
                threading.Thread(
                    target=lambda: (
                        time.sleep(0.25),
                        os._exit(0),
                    ),
                    daemon=True,
                ).start()

            threading.Thread(target=stop_when_inference_finishes, daemon=True).start()
            return True

        def idle_watchdog() -> None:
            while True:
                time.sleep(15)
                with activity_lock:
                    elapsed = time.monotonic() - last_activity
                    should_shutdown = (
                        elapsed >= idle_minutes * 60
                        and active_requests == 0
                        and not stopping
                    )
                if should_shutdown and request_shutdown("idle", require_idle=True):
                    emit("idle_shutdown", idle_minutes=idle_minutes)
                    return

        @app.middleware("http")
        async def secure_loopback_request(request: Request, call_next):
            if not is_loopback_host_header(request.headers.get("host")):
                return security_response(403, "Forbidden: non-loopback Host header.")
            if not is_authorized(request.headers.get("authorization"), auth_token):
                return security_response(401, "Unauthorized: missing or invalid bearer token.")
            if request.method in {"POST", "PUT", "PATCH"}:
                try:
                    require_json_content_type(request.headers.get("content-type"))
                    checked_content_length(
                        request.headers.get("content-length"),
                        maximum_bytes=MAX_REQUEST_BYTES,
                    )
                except RequestRejected as exc:
                    return security_response(public_error_code(exc), str(exc))
            return await call_next(request)

        @app.exception_handler(RequestRejected)
        async def rejected_request(_request: Request, exc: RequestRejected):
            return security_response(public_error_code(exc), str(exc))

        @app.exception_handler(RequestValidationError)
        async def invalid_request(_request: Request, _exc: RequestValidationError):
            return security_response(422, "Invalid request payload.")

        @app.get("/health")
        def health():
            return {"status": "ok", "engine": "higgs-tts-3-4b"}

        @app.post("/say")
        def say(request: SayRequest):
            begin_activity()
            try:
                output_path = validate_output_wav(request.output)
                audio = bundle.synthesize_chunked(request.text, None, None, "")
                bundle.write_wav(audio, output_path)
                return {"output": str(output_path), "engine": "higgs-tts-3-4b"}
            except RequestRejected as exc:
                raise exc
            except Exception as exc:
                code = public_error_code(exc)
                emit("request_error", operation="say", code=code)
                raise HTTPException(
                    status_code=500,
                    detail={"error": "TTS generation failed.", "code": code},
                ) from None
            finally:
                end_activity()

        @app.post("/clone")
        def clone(request: CloneRequest):
            begin_activity()
            try:
                output_path = validate_output_wav(request.output)
                reference_audio = validate_file(
                    request.ref_audio,
                    field="ref_audio",
                    allowed_suffixes={".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".webm"},
                    maximum_bytes=args.max_reference_audio_megabytes * 1024 * 1024,
                )
                wav, sample_rate = load_reference_audio(reference_audio)
                audio = bundle.synthesize_chunked(request.text, wav, sample_rate, request.ref_text)
                bundle.write_wav(audio, output_path)
                return {"output": str(output_path), "engine": "higgs-tts-3-4b"}
            except RequestRejected as exc:
                raise exc
            except Exception as exc:
                code = public_error_code(exc)
                emit("request_error", operation="clone", code=code)
                raise HTTPException(
                    status_code=500,
                    detail={"error": "TTS generation failed.", "code": code},
                ) from None
            finally:
                end_activity()

        @app.post("/shutdown")
        def shutdown():
            if request_shutdown("api"):
                return {"status": "stopping"}
            return {"status": "already-stopping"}

        threading.Thread(target=idle_watchdog, daemon=True).start()
        emit(
            "ready",
            engine="higgs-tts-3-4b",
            model=str(model_dir),
            device=str(device),
            idle_minutes=idle_minutes,
            port=args.port,
        )
        uvicorn.run(app, host=host, port=int(args.port), log_level="warning", access_log=False)
        return 0
    except Exception as exc:
        emit("fatal", code=public_error_code(exc), error=str(exc))
        return 1


def run_one_shot(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Higgs Audio v3 TTS one-shot.")
    sub = parser.add_subparsers(dest="command", required=True)
    say_p = sub.add_parser("say")
    say_p.add_argument("--text", required=True)
    say_p.add_argument("--out", required=True)
    say_p.add_argument("--model", required=True)
    say_p.add_argument("--device", default="auto")
    clone_p = sub.add_parser("clone")
    clone_p.add_argument("--text", required=True)
    clone_p.add_argument("--ref-audio", required=True)
    clone_p.add_argument("--out", required=True)
    clone_p.add_argument("--model", required=True)
    clone_p.add_argument("--device", default="auto")
    args = parser.parse_args(argv)
    try:
        model_dir = Path(args.model)
        device = _resolve_device(args.device)
        print(f"Loading Higgs Audio v3 from {model_dir} on {device} (bf16)...", flush=True)
        bundle = HiggsTtsBundle(model_dir, device)
        print("Model ready. Generating...", flush=True)
        output = Path(args.out)
        if args.command == "clone":
            ref_path = validate_file(
                args.ref_audio,
                field="ref_audio",
                allowed_suffixes={".wav", ".flac", ".mp3", ".ogg", ".opus", ".m4a"},
                maximum_bytes=512 * 1024 * 1024,
            )
            wav, sample_rate = load_reference_audio(ref_path)
            audio = bundle.synthesize_chunked(args.text, wav, sample_rate, "")
        else:
            audio = bundle.synthesize_chunked(args.text, None, None, "")
        bundle.write_wav(audio, output)
        print(f"Wrote {output} ({audio.numel() / SAMPLE_RATE:.1f}s)", flush=True)
        return 0
    except Exception as exc:
        print(f"fatal: {exc}", flush=True)
        return 1


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] in {"say", "clone"}:
        sys.exit(run_one_shot(sys.argv[1:]))
    sys.exit(main())

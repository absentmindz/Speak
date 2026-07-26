from __future__ import annotations

import argparse
import os
import sys
import threading
import time
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from fastapi import FastAPI, HTTPException, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
import uvicorn

from qwen_tts_runtime import canonical_choice, emit, load_qwen_model, write_wav
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
MAX_INSTRUCTION_CHARS = 4_000
MAX_PATH_CHARS = 32_767
MAX_KEEP_ALIVE_MINUTES = 24 * 60
SUPPORTED_AUDIO_SUFFIXES = {".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".webm"}


class WorkerRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True, str_strip_whitespace=True)


class SayRequest(WorkerRequest):
    text: str = Field(min_length=1, max_length=MAX_TEXT_CHARS)
    output: str = Field(min_length=1, max_length=MAX_PATH_CHARS)
    speaker: str = Field(default="Aiden", max_length=128)
    language: str = Field(default="English", max_length=64)
    instruct: str = Field(default="", max_length=MAX_INSTRUCTION_CHARS)
    voice_prompt_path: str = Field(default="", alias="voicePromptPath", max_length=MAX_PATH_CHARS)


class CloneRequest(WorkerRequest):
    text: str = Field(min_length=1, max_length=MAX_TEXT_CHARS)
    output: str = Field(min_length=1, max_length=MAX_PATH_CHARS)
    ref_audio: str = Field(min_length=1, max_length=MAX_PATH_CHARS)
    language: str = Field(default="Auto", max_length=64)
    ref_text: str = Field(default="", max_length=MAX_TEXT_CHARS)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Warm local Qwen3 TTS worker.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8766)
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
        content={"ok": False, "error": message},
        headers={
            "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff",
        },
    )


def main() -> int:
    args = parse_args()
    host = ensure_loopback_host(args.host)
    if not 1 <= args.port <= 65_535:
        raise SystemExit("Worker port must be between 1 and 65535.")
    if not 0 < args.idle_minutes <= MAX_KEEP_ALIVE_MINUTES:
        raise SystemExit("Idle timeout is outside the supported range.")
    if not 1 <= args.max_reference_audio_megabytes <= 4096:
        raise SystemExit("Maximum reference-audio size is outside the supported range.")
    if not 1 <= args.startup_timeout_seconds <= 3600:
        raise SystemExit("Startup timeout is outside the supported range.")

    auth_token = resolve_auth_token(args.auth_token)
    tts, device = load_qwen_model(args.model, args.device)
    supported_speakers = (
        tts.model.get_supported_speakers()
        if callable(getattr(tts.model, "get_supported_speakers", None))
        else None
    )
    supported_languages = (
        tts.model.get_supported_languages()
        if callable(getattr(tts.model, "get_supported_languages", None))
        else None
    )
    idle_timeout_seconds = max(1.0, args.idle_minutes * 60.0)
    max_reference_audio_bytes = args.max_reference_audio_megabytes * 1024 * 1024
    state_lock = threading.Lock()
    inference_lock = threading.Lock()
    state: dict[str, Any] = {
        "last_activity": time.monotonic(),
        "active_requests": 0,
        "stopping": False,
    }
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)
    server: uvicorn.Server

    def begin_activity() -> None:
        with state_lock:
            if state["stopping"]:
                raise RequestRejected(503, "TTS worker is stopping.")
            state["active_requests"] += 1
            state["last_activity"] = time.monotonic()

    def end_activity() -> None:
        with state_lock:
            state["active_requests"] = max(0, state["active_requests"] - 1)
            state["last_activity"] = time.monotonic()

    def request_shutdown(reason: str) -> bool:
        with state_lock:
            if state["stopping"]:
                return False
            state["stopping"] = True

        def stop_when_inference_finishes() -> None:
            with inference_lock:
                emit("stopping", reason=reason)
                server.should_exit = True

        threading.Thread(target=stop_when_inference_finishes, daemon=True).start()
        return True

    def idle_watchdog() -> None:
        while True:
            time.sleep(2)
            with state_lock:
                idle_for = time.monotonic() - state["last_activity"]
                should_stop = (
                    not state["stopping"]
                    and state["active_requests"] == 0
                    and idle_for >= idle_timeout_seconds
                )
            if should_stop:
                emit(
                    "idle_shutdown",
                    idle_seconds=round(idle_for, 2),
                    idle_timeout_seconds=idle_timeout_seconds,
                )
                request_shutdown("idle")
                return

    @app.middleware("http")
    async def secure_loopback_request(request: Request, call_next):
        try:
            if not is_loopback_host_header(request.headers.get("host")):
                raise RequestRejected(400, "Invalid Host header.")
            if not is_authorized(request.headers.get("authorization"), auth_token):
                raise RequestRejected(401, "Authentication required.")
            if request.method in {"POST", "PUT", "PATCH"}:
                if request.headers.get("transfer-encoding"):
                    raise RequestRejected(400, "Transfer-Encoding is not supported.")
                require_json_content_type(request.headers.get("content-type"))
                checked_content_length(
                    request.headers.get("content-length"),
                    maximum_bytes=MAX_REQUEST_BYTES,
                )
        except RequestRejected as exc:
            response = security_response(exc.status, exc.public_message)
            if exc.status == 401:
                response.headers["WWW-Authenticate"] = 'Bearer realm="Speak worker"'
            return response

        response = await call_next(request)
        response.headers["Cache-Control"] = "no-store"
        response.headers["X-Content-Type-Options"] = "nosniff"
        return response

    @app.exception_handler(RequestRejected)
    async def rejected_request(_request: Request, exc: RequestRejected):
        return security_response(exc.status, exc.public_message)

    @app.exception_handler(RequestValidationError)
    async def invalid_request(_request: Request, _exc: RequestValidationError):
        return security_response(422, "Request validation failed.")

    @app.get("/health")
    def health():
        with state_lock:
            idle_for = time.monotonic() - state["last_activity"]
            return {
                "ok": not state["stopping"],
                "device": device,
                "model": Path(args.model).name,
                "idleTimeoutSeconds": idle_timeout_seconds,
                "idleForSeconds": round(idle_for, 2),
                "activeRequests": state["active_requests"],
                "stopping": state["stopping"],
                "authenticationRequired": True,
            }

    @app.post("/say")
    def say(request: SayRequest):
        begin_activity()
        acquired = inference_lock.acquire(blocking=False)
        try:
            if not acquired:
                raise RequestRejected(429, "TTS worker is busy.")
            output_path = validate_output_wav(request.output)
            speaker = canonical_choice(request.speaker, supported_speakers, "Aiden")
            language = canonical_choice(request.language, supported_languages, "English")
            wavs, sample_rate = tts.generate_custom_voice(
                text=request.text,
                language=language,
                speaker=speaker,
                instruct=request.instruct or None,
            )
            if not wavs:
                raise RuntimeError("TTS model returned no audio")
            output = write_wav(str(output_path), wavs[0], sample_rate)
            return {
                "output": output,
                "device": device,
                "speaker": speaker,
                "language": language,
                "sampleRate": sample_rate,
            }
        except RequestRejected:
            raise
        except Exception as exc:
            code = public_error_code(exc)
            emit("request_error", operation="say", code=code)
            raise HTTPException(
                status_code=500,
                detail={"error": "TTS generation failed.", "code": code},
            ) from None
        finally:
            if acquired:
                inference_lock.release()
            end_activity()

    @app.post("/clone")
    def clone(request: CloneRequest):
        begin_activity()
        acquired = inference_lock.acquire(blocking=False)
        try:
            if not acquired:
                raise RequestRejected(429, "TTS worker is busy.")
            output_path = validate_output_wav(request.output)
            reference_audio = validate_file(
                request.ref_audio,
                field="ref_audio",
                maximum_bytes=max_reference_audio_bytes,
                allowed_suffixes=SUPPORTED_AUDIO_SUFFIXES,
            )
            language = canonical_choice(request.language, supported_languages, "Auto")
            ref_text = request.ref_text or None
            wavs, sample_rate = tts.generate_voice_clone(
                text=request.text,
                language=language,
                ref_audio=str(reference_audio),
                ref_text=ref_text,
                x_vector_only_mode=ref_text is None,
            )
            if not wavs:
                raise RuntimeError("TTS model returned no audio")
            output = write_wav(str(output_path), wavs[0], sample_rate)
            return {
                "output": output,
                "device": device,
                "language": language,
                "sampleRate": sample_rate,
            }
        except RequestRejected:
            raise
        except Exception as exc:
            code = public_error_code(exc)
            emit("request_error", operation="clone", code=code)
            raise HTTPException(
                status_code=500,
                detail={"error": "Voice cloning failed.", "code": code},
            ) from None
        finally:
            if acquired:
                inference_lock.release()
            end_activity()

    @app.post("/shutdown")
    def shutdown():
        started = request_shutdown("requested")
        return {"ok": True, "stopping": True, "alreadyStopping": not started}

    config = uvicorn.Config(
        app,
        host=host,
        port=args.port,
        log_level="warning",
        access_log=False,
        server_header=False,
        date_header=False,
        timeout_keep_alive=5,
        limit_concurrency=8,
        backlog=16,
        h11_max_incomplete_event_size=MAX_REQUEST_BYTES + 16 * 1024,
    )
    server = uvicorn.Server(config)
    threading.Thread(target=idle_watchdog, daemon=True).start()
    emit(
        "ready",
        worker="qwen3-tts",
        device=device,
        host=host,
        port=args.port,
        idle_timeout_seconds=idle_timeout_seconds,
        authentication_required=True,
    )
    server.run()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        emit("fatal", code=public_error_code(exc))
        raise SystemExit(1) from None

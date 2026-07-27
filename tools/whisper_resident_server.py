from __future__ import annotations

import argparse
import gc
import json
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit

import torch
import whisper

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
    validate_text,
)


MAX_REQUEST_BYTES = 128 * 1024
MAX_TRANSCRIPT_CHARS = 2_000_000
MAX_KEEP_ALIVE_MINUTES = 24 * 60
SUPPORTED_AUDIO_SUFFIXES = {
    ".aac",
    ".flac",
    ".m4a",
    ".mp3",
    ".mp4",
    ".ogg",
    ".opus",
    ".wav",
    ".webm",
    ".wma",
}


class WhisperState:
    def __init__(self, idle_minutes: float, model_dir: str, max_audio_bytes: int):
        self.lock = threading.Lock()
        self.load_lock = threading.Lock()
        self.inference_lock = threading.Lock()
        self.model = None
        self.model_name = ""
        self.loaded_model_dir = ""
        self.device = ""
        self.model_dir = model_dir
        self.max_audio_bytes = max_audio_bytes
        self.idle_timeout_seconds = max(1.0, idle_minutes * 60.0)
        self.last_activity = time.monotonic()
        self.is_model_loading = False
        self.active_requests = 0
        self.active_transcriptions = 0
        self.stopping = False


class WorkerHttpServer(ThreadingHTTPServer):
    daemon_threads = True
    request_queue_size = 8


STATE: WhisperState
SERVER: WorkerHttpServer
AUTH_TOKEN = ""


def emit(event: str, **values: Any) -> None:
    print(json.dumps({"event": event, **values}, ensure_ascii=True), flush=True)


def resolve_device(requested: str) -> str:
    requested = (requested or "auto").lower()
    cuda_available = torch.cuda.is_available()
    if requested == "auto":
        return "cuda" if cuda_available else "cpu"
    if requested == "cuda" and not cuda_available:
        raise RequestRejected(400, "CUDA was requested but is not available in this worker.")
    if requested not in {"cuda", "cpu"}:
        raise RequestRejected(400, "Unsupported Whisper device.")
    return requested


def unload_model() -> None:
    with STATE.lock:
        was_loaded = STATE.model is not None
        STATE.model = None
        STATE.model_name = ""
        STATE.loaded_model_dir = ""
        STATE.device = ""
    if not was_loaded:
        return
    gc.collect()
    if torch.cuda.is_available():
        try:
            torch.cuda.synchronize()
        finally:
            torch.cuda.empty_cache()


def unload_model_if_idle() -> None:
    model = None
    with STATE.load_lock:
        with STATE.lock:
            idle_for = time.monotonic() - STATE.last_activity
            should_unload = (
                STATE.model is not None
                and STATE.active_requests == 0
                and not STATE.is_model_loading
                and not STATE.stopping
                and idle_for >= STATE.idle_timeout_seconds
            )
            if should_unload:
                model = STATE.model
                STATE.model = None
                STATE.model_name = ""
                STATE.loaded_model_dir = ""
                STATE.device = ""

    if model is None:
        return

    del model
    gc.collect()
    if torch.cuda.is_available():
        try:
            torch.cuda.synchronize()
        finally:
            torch.cuda.empty_cache()
    emit("model_unloaded", reason="idle")


def load_model(model_name: str, device: str, model_dir: str):
    with STATE.load_lock:
        with STATE.lock:
            if (
                STATE.model is not None
                and STATE.model_name == model_name
                and STATE.device == device
                and STATE.loaded_model_dir == model_dir
            ):
                STATE.last_activity = time.monotonic()
                return STATE.model
            STATE.is_model_loading = True
            STATE.last_activity = time.monotonic()
        try:
            unload_model()
            emit("loading", model=Path(model_name).name, device=device)
            loaded = whisper.load_model(model_name, device=device, download_root=model_dir)
            with STATE.lock:
                STATE.model = loaded
                STATE.model_name = model_name
                STATE.loaded_model_dir = model_dir
                STATE.device = device
                STATE.last_activity = time.monotonic()
                return STATE.model
        finally:
            with STATE.lock:
                STATE.is_model_loading = False


def begin_request(*, transcription: bool) -> None:
    with STATE.lock:
        if STATE.stopping:
            raise RequestRejected(503, "Whisper worker is stopping.")
        STATE.last_activity = time.monotonic()
        STATE.active_requests += 1
        if transcription:
            STATE.active_transcriptions += 1


def end_request(*, transcription: bool) -> None:
    with STATE.lock:
        STATE.active_requests = max(0, STATE.active_requests - 1)
        if transcription:
            STATE.active_transcriptions = max(0, STATE.active_transcriptions - 1)
        STATE.last_activity = time.monotonic()


def stop_server(reason: str) -> None:
    with STATE.lock:
        if STATE.stopping:
            return
        STATE.stopping = True
    emit("stopping", reason=reason)
    with STATE.inference_lock:
        unload_model()
    SERVER.shutdown()


def request_stop(reason: str) -> None:
    threading.Thread(target=stop_server, args=(reason,), daemon=True).start()


def idle_watchdog() -> None:
    while True:
        time.sleep(2)
        with STATE.lock:
            if STATE.stopping:
                return
        unload_model_if_idle()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "SpeakWorker"
    sys_version = ""

    def setup(self) -> None:
        super().setup()
        self.connection.settimeout(15)

    def log_message(self, format, *args):  # noqa: A002
        return

    def version_string(self) -> str:
        return self.server_version

    def _request_path(self) -> str:
        return urlsplit(self.path).path

    def _validate_request_origin(self) -> None:
        if not is_loopback_host_header(self.headers.get("Host")):
            raise RequestRejected(400, "Invalid Host header.")
        if not is_authorized(self.headers.get("Authorization"), AUTH_TOKEN):
            raise RequestRejected(401, "Authentication required.")

    def _read_json(self) -> dict[str, Any]:
        if self.headers.get("Transfer-Encoding"):
            raise RequestRejected(400, "Transfer-Encoding is not supported.")
        require_json_content_type(self.headers.get("Content-Type"))
        length = checked_content_length(
            self.headers.get("Content-Length"),
            maximum_bytes=MAX_REQUEST_BYTES,
        )
        try:
            body = self.rfile.read(length)
        except TimeoutError as exc:
            raise RequestRejected(408, "Request body timed out.") from exc
        if len(body) != length:
            raise RequestRejected(400, "Request body is incomplete.")
        try:
            payload = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise RequestRejected(400, "Request body must be valid UTF-8 JSON.") from exc
        if not isinstance(payload, dict):
            raise RequestRejected(400, "Request JSON must be an object.")
        return payload

    def _send_json(self, status: int, payload: Any) -> None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        if status == 401:
            self.send_header("WWW-Authenticate", 'Bearer realm="Speak worker"')
        self.end_headers()
        try:
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            pass

    def _send_rejection(self, exc: RequestRejected) -> None:
        self._send_json(exc.status, {"ok": False, "error": exc.public_message})

    def do_GET(self):  # noqa: N802
        try:
            self._validate_request_origin()
            if self._request_path() != "/health":
                raise RequestRejected(404, "Not found.")

            with STATE.lock:
                idle_for = time.monotonic() - STATE.last_activity
                payload = {
                    "ok": not STATE.stopping,
                    "modelLoaded": STATE.model is not None,
                    "modelLoading": STATE.is_model_loading,
                    "model": Path(STATE.model_name).name,
                    "device": STATE.device,
                    "cudaAvailable": torch.cuda.is_available(),
                    "torch": getattr(torch, "__version__", "unknown"),
                    "idleTimeoutSeconds": STATE.idle_timeout_seconds,
                    "idleForSeconds": round(idle_for, 2),
                    "isTranscribing": STATE.active_transcriptions > 0,
                    "activeTranscriptions": STATE.active_transcriptions,
                    "activeRequests": STATE.active_requests,
                    "stopping": STATE.stopping,
                    "authenticationRequired": bool(AUTH_TOKEN),
                }
            self._send_json(200, payload)
        except RequestRejected as exc:
            self._send_rejection(exc)

    def do_POST(self):  # noqa: N802
        request_started = False
        is_transcription = False
        try:
            self._validate_request_origin()
            path = self._request_path()
            if path not in {"/stop", "/transcribe", "/load"}:
                raise RequestRejected(404, "Not found.")
            payload = self._read_json()

            if path == "/stop":
                self._send_json(200, {"ok": True, "stopping": True})
                threading.Timer(0.1, request_stop, args=("requested",)).start()
                return

            allowed_fields = {"model", "modelDir", "device", "keepAliveMinutes"}
            if path == "/transcribe":
                allowed_fields.update({"audioPath", "language"})
            unknown = sorted(set(payload) - allowed_fields)
            if unknown:
                raise RequestRejected(400, "Request contains unsupported fields.")

            model_name = validate_text(
                payload.get("model") or "base",
                field="model",
                maximum_chars=256,
            )
            model_dir = validate_text(
                payload.get("modelDir") or STATE.model_dir,
                field="modelDir",
                maximum_chars=32_767,
            )
            requested_device = validate_text(
                payload.get("device") or "auto",
                field="device",
                maximum_chars=16,
            )
            raw_keep_alive = payload.get("keepAliveMinutes", 10)
            if isinstance(raw_keep_alive, bool):
                raise RequestRejected(400, "keepAliveMinutes is invalid.")
            try:
                keep_alive_minutes = float(raw_keep_alive)
            except (TypeError, ValueError) as exc:
                raise RequestRejected(400, "keepAliveMinutes is invalid.") from exc
            if not 0 < keep_alive_minutes <= MAX_KEEP_ALIVE_MINUTES:
                raise RequestRejected(400, "keepAliveMinutes is outside the supported range.")
            device = resolve_device(requested_device)

            with STATE.lock:
                STATE.model_dir = model_dir
                STATE.idle_timeout_seconds = max(1.0, keep_alive_minutes * 60.0)

            is_transcription = path == "/transcribe"
            begin_request(transcription=is_transcription)
            request_started = True
            started = time.monotonic()

            if path == "/load":
                with STATE.inference_lock:
                    load_model(model_name, device, model_dir)
                self._send_json(
                    200,
                    {
                        "ok": True,
                        "modelLoaded": True,
                        "model": Path(model_name).name,
                        "device": device,
                        "cudaAvailable": torch.cuda.is_available(),
                        "torch": getattr(torch, "__version__", "unknown"),
                        "elapsedSeconds": round(time.monotonic() - started, 2),
                        "idleTimeoutSeconds": STATE.idle_timeout_seconds,
                    },
                )
                return

            audio_path = validate_file(
                payload.get("audioPath"),
                field="audioPath",
                maximum_bytes=STATE.max_audio_bytes,
                allowed_suffixes=SUPPORTED_AUDIO_SUFFIXES,
            )
            language_text = validate_text(
                payload.get("language") or "",
                field="language",
                maximum_chars=32,
                allow_empty=True,
            )
            language = language_text or None

            with STATE.inference_lock:
                model = load_model(model_name, device, model_dir)
                result = model.transcribe(
                    str(audio_path),
                    language=language,
                    fp16=(device == "cuda"),
                    verbose=False,
                )
            text = (result.get("text") or "").strip()
            if not text:
                raise RuntimeError("empty transcript")
            if len(text) > MAX_TRANSCRIPT_CHARS:
                raise RuntimeError("transcript exceeds response limit")

            self._send_json(
                200,
                {
                    "ok": True,
                    "text": text,
                    "model": Path(model_name).name,
                    "device": device,
                    "cudaAvailable": torch.cuda.is_available(),
                    "torch": getattr(torch, "__version__", "unknown"),
                    "elapsedSeconds": round(time.monotonic() - started, 2),
                    "idleTimeoutSeconds": STATE.idle_timeout_seconds,
                },
            )
        except RequestRejected as exc:
            self._send_rejection(exc)
        except Exception as exc:  # noqa: BLE001
            code = public_error_code(exc)
            emit("request_error", operation=self._request_path().lstrip("/"), code=code)
            self._send_json(
                500,
                {
                    "ok": False,
                    "error": "Whisper worker request failed.",
                    "code": code,
                },
            )
        finally:
            if request_started:
                end_request(transcription=is_transcription)

    def do_OPTIONS(self):  # noqa: N802
        self._send_json(405, {"ok": False, "error": "Method not allowed."})

    def do_PUT(self):  # noqa: N802
        self._send_json(405, {"ok": False, "error": "Method not allowed."})

    def do_DELETE(self):  # noqa: N802
        self._send_json(405, {"ok": False, "error": "Method not allowed."})


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Speak resident Whisper worker.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=39731)
    parser.add_argument("--idle-minutes", type=float, default=10)
    parser.add_argument("--model-dir", default=str(Path.home() / ".cache" / "whisper"))
    parser.add_argument("--max-audio-megabytes", type=int, default=512)
    parser.add_argument("--auth-token", default=None, help=argparse.SUPPRESS)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    host = ensure_loopback_host(args.host)
    if not 1 <= args.port <= 65_535:
        raise SystemExit("Worker port must be between 1 and 65535.")
    if not 0 < args.idle_minutes <= MAX_KEEP_ALIVE_MINUTES:
        raise SystemExit("Idle timeout is outside the supported range.")
    if not 1 <= args.max_audio_megabytes <= 4096:
        raise SystemExit("Maximum audio size is outside the supported range.")

    global AUTH_TOKEN, SERVER, STATE
    AUTH_TOKEN = resolve_auth_token(args.auth_token)
    STATE = WhisperState(
        args.idle_minutes,
        args.model_dir,
        args.max_audio_megabytes * 1024 * 1024,
    )
    SERVER = WorkerHttpServer((host, args.port), Handler)

    threading.Thread(target=idle_watchdog, daemon=True).start()
    emit(
        "ready",
        worker="whisper",
        host=host,
        port=args.port,
        authentication_required=True,
    )
    try:
        SERVER.serve_forever(poll_interval=0.25)
    finally:
        SERVER.server_close()
        unload_model()


if __name__ == "__main__":
    main()

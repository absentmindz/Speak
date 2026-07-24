import argparse
import gc
import json
import os
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import torch
import whisper


class WhisperState:
    def __init__(self, idle_minutes: float, model_dir: str):
        self.lock = threading.Lock()
        self.load_lock = threading.Lock()
        self.model = None
        self.model_name = ""
        self.device = ""
        self.model_dir = model_dir
        self.idle_timeout_seconds = max(1.0, idle_minutes * 60.0)
        self.last_activity = time.monotonic()
        self.is_model_loading = False
        self.active_transcriptions = 0


STATE: WhisperState


def resolve_device(requested: str) -> str:
    requested = (requested or "auto").lower()
    cuda_available = torch.cuda.is_available()
    if requested == "auto":
        return "cuda" if cuda_available else "cpu"
    if requested == "cuda" and not cuda_available:
        raise RuntimeError(
            "GPU/CUDA was selected, but torch.cuda.is_available() is false. "
            f"Current torch build: {getattr(torch, '__version__', 'unknown')}. "
            "Install a CUDA-enabled PyTorch build in the Whisper Python environment to use GPU."
        )
    if requested not in {"cuda", "cpu"}:
        raise RuntimeError(f"Unsupported Whisper device: {requested}")
    return requested


def unload_model() -> None:
    with STATE.lock:
        was_loaded = STATE.model is not None
        STATE.model = None
        STATE.model_name = ""
        STATE.device = ""
    if not was_loaded:
        return
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.synchronize()
        torch.cuda.empty_cache()


def unload_model_if_idle() -> None:
    model = None
    with STATE.load_lock:
        with STATE.lock:
            idle_for = time.monotonic() - STATE.last_activity
            should_unload = (
                STATE.model is not None
                and STATE.active_transcriptions == 0
                and not STATE.is_model_loading
                and idle_for >= STATE.idle_timeout_seconds
            )
            if should_unload:
                model = STATE.model
                STATE.model = None
                STATE.model_name = ""
                STATE.device = ""

    if model is None:
        return

    del model
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.synchronize()
        torch.cuda.empty_cache()


def load_model(model_name: str, device: str):
    with STATE.load_lock:
        with STATE.lock:
            if STATE.model is not None and STATE.model_name == model_name and STATE.device == device:
                STATE.last_activity = time.monotonic()
                return STATE.model
            STATE.is_model_loading = True
            STATE.last_activity = time.monotonic()
        try:
            unload_model()
            print(json.dumps({"event": "loading", "model": model_name, "device": device}), flush=True)
            loaded = whisper.load_model(model_name, device=device, download_root=STATE.model_dir)
            with STATE.lock:
                STATE.model = loaded
                STATE.model_name = model_name
                STATE.device = device
                STATE.last_activity = time.monotonic()
                return STATE.model
        finally:
            with STATE.lock:
                STATE.is_model_loading = False


def idle_watchdog() -> None:
    while True:
        time.sleep(2)
        unload_model_if_idle()


def stop_process_soon() -> None:
    unload_model()
    time.sleep(0.2)
    os._exit(0)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, format, *args):  # noqa: A002
        return

    def _read_json(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length) if length else b"{}"
        return json.loads(body.decode("utf-8"))

    def _send_json(self, status: int, payload) -> None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):  # noqa: N802
        if self.path != "/health":
            self._send_json(404, {"error": "not found"})
            return

        with STATE.lock:
            idle_for = time.monotonic() - STATE.last_activity
            payload = {
                "ok": True,
                "modelLoaded": STATE.model is not None,
                "modelLoading": STATE.is_model_loading,
                "model": STATE.model_name,
                "device": STATE.device,
                "cudaAvailable": torch.cuda.is_available(),
                "torch": getattr(torch, "__version__", "unknown"),
                "idleTimeoutSeconds": STATE.idle_timeout_seconds,
                "idleForSeconds": round(idle_for, 2),
                "isTranscribing": STATE.active_transcriptions > 0,
                "activeTranscriptions": STATE.active_transcriptions,
            }
        self._send_json(200, payload)

    def do_POST(self):  # noqa: N802
        if self.path == "/stop":
            self._send_json(200, {"ok": True, "stopping": True})
            threading.Thread(target=stop_process_soon, daemon=True).start()
            return

        if self.path not in {"/transcribe", "/load"}:
            self._send_json(404, {"error": "not found"})
            return

        try:
            payload = self._read_json()
            model_name = payload.get("model") or "base"
            model_dir = payload.get("modelDir") or STATE.model_dir
            requested_device = payload.get("device") or "auto"
            keep_alive_minutes = float(payload.get("keepAliveMinutes") or 10)

            STATE.model_dir = model_dir
            STATE.idle_timeout_seconds = max(1.0, keep_alive_minutes * 60.0)
            device = resolve_device(requested_device)

            if self.path == "/load":
                started = time.monotonic()
                load_model(model_name, device)
                self._send_json(
                    200,
                    {
                        "ok": True,
                        "modelLoaded": True,
                        "model": model_name,
                        "device": device,
                        "cudaAvailable": torch.cuda.is_available(),
                        "torch": getattr(torch, "__version__", "unknown"),
                        "elapsedSeconds": round(time.monotonic() - started, 2),
                        "idleTimeoutSeconds": STATE.idle_timeout_seconds,
                    },
                )
                return

            audio_path = payload.get("audioPath") or ""
            language = payload.get("language") or None
            if not os.path.exists(audio_path):
                raise RuntimeError(f"Audio file not found: {audio_path}")

            with STATE.lock:
                STATE.last_activity = time.monotonic()
                STATE.active_transcriptions += 1

            try:
                started = time.monotonic()
                model = load_model(model_name, device)
                result = model.transcribe(
                    audio_path,
                    language=language,
                    fp16=(device == "cuda"),
                    verbose=False,
                )
                elapsed = round(time.monotonic() - started, 2)
                text = (result.get("text") or "").strip()
                if not text:
                    raise RuntimeError("Whisper produced an empty transcript.")
            finally:
                with STATE.lock:
                    STATE.active_transcriptions = max(0, STATE.active_transcriptions - 1)
                    STATE.last_activity = time.monotonic()

            self._send_json(
                200,
                {
                    "ok": True,
                    "text": text,
                    "model": model_name,
                    "device": device,
                    "cudaAvailable": torch.cuda.is_available(),
                    "torch": getattr(torch, "__version__", "unknown"),
                    "elapsedSeconds": elapsed,
                    "idleTimeoutSeconds": STATE.idle_timeout_seconds,
                },
            )
        except Exception as exc:  # noqa: BLE001
            with STATE.lock:
                STATE.active_transcriptions = max(0, STATE.active_transcriptions - 1)
                STATE.last_activity = time.monotonic()
                STATE.is_model_loading = False
            self._send_json(500, {"ok": False, "error": str(exc)})


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=39731)
    parser.add_argument("--idle-minutes", type=float, default=10)
    parser.add_argument("--model-dir", default=r"D:\Models\whisper")
    args = parser.parse_args()

    global STATE
    STATE = WhisperState(args.idle_minutes, args.model_dir)

    threading.Thread(target=idle_watchdog, daemon=True).start()
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"MAXFLOW_WHISPER_SERVER_READY http://{args.host}:{args.port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()

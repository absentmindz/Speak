import argparse
import os
import sys
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import uvicorn

from qwen_tts_runtime import canonical_choice, emit, load_qwen_model, write_wav


class SayRequest(BaseModel):
    text: str
    output: str
    speaker: str = "Aiden"
    language: str = "English"
    instruct: str = ""


class CloneRequest(BaseModel):
    text: str
    output: str
    ref_audio: str
    language: str = "Auto"
    ref_text: str = ""


def parse_args():
    parser = argparse.ArgumentParser(description="Warm local Qwen3 TTS worker.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8766)
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--startup-timeout-seconds", type=int, default=600)
    parser.add_argument("--idle-minutes", type=float, default=10)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    tts, device = load_qwen_model(args.model, args.device)
    supported_speakers = tts.model.get_supported_speakers() if callable(getattr(tts.model, "get_supported_speakers", None)) else None
    supported_languages = tts.model.get_supported_languages() if callable(getattr(tts.model, "get_supported_languages", None)) else None
    idle_timeout_seconds = max(1.0, args.idle_minutes * 60.0)
    state_lock = threading.Lock()
    state = {
        "last_activity": time.monotonic(),
        "active_requests": 0,
        "stopping": False,
    }
    app = FastAPI()

    def begin_activity():
        with state_lock:
            if state["stopping"]:
                raise HTTPException(status_code=503, detail="TTS worker is stopping after its idle timeout.")
            state["active_requests"] += 1

    def end_activity():
        with state_lock:
            state["active_requests"] = max(0, state["active_requests"] - 1)
            state["last_activity"] = time.monotonic()

    def idle_watchdog():
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
                    state["stopping"] = True
            if should_stop:
                emit("idle_shutdown", idle_seconds=round(idle_for, 2), idle_timeout_seconds=idle_timeout_seconds)
                os._exit(0)

    @app.get("/health")
    def health():
        with state_lock:
            idle_for = time.monotonic() - state["last_activity"]
            return {
                "ok": not state["stopping"],
                "device": device,
                "model": args.model,
                "idleTimeoutSeconds": idle_timeout_seconds,
                "idleForSeconds": round(idle_for, 2),
                "activeRequests": state["active_requests"],
                "stopping": state["stopping"],
            }

    @app.post("/say")
    def say(request: SayRequest):
        begin_activity()
        try:
            speaker = canonical_choice(request.speaker, supported_speakers, "Aiden")
            language = canonical_choice(request.language, supported_languages, "English")
            wavs, sample_rate = tts.generate_custom_voice(
                text=request.text,
                language=language,
                speaker=speaker,
                instruct=request.instruct.strip() or None,
            )
            output = write_wav(request.output, wavs[0], sample_rate)
            return {"output": output, "device": device, "speaker": speaker, "language": language, "sampleRate": sample_rate}
        except Exception as exc:
            raise HTTPException(status_code=500, detail=f"{type(exc).__name__}: {exc}") from exc
        finally:
            end_activity()

    @app.post("/clone")
    def clone(request: CloneRequest):
        begin_activity()
        try:
            language = canonical_choice(request.language, supported_languages, "Auto")
            ref_text = request.ref_text.strip() or None
            wavs, sample_rate = tts.generate_voice_clone(
                text=request.text,
                language=language,
                ref_audio=request.ref_audio,
                ref_text=ref_text,
                x_vector_only_mode=ref_text is None,
            )
            output = write_wav(request.output, wavs[0], sample_rate)
            return {"output": output, "device": device, "language": language, "sampleRate": sample_rate}
        except Exception as exc:
            raise HTTPException(status_code=500, detail=f"{type(exc).__name__}: {exc}") from exc
        finally:
            end_activity()

    @app.post("/shutdown")
    def shutdown():
        with state_lock:
            state["stopping"] = True
        threading.Timer(0.2, lambda: os._exit(0)).start()
        return {"ok": True}

    threading.Thread(target=idle_watchdog, daemon=True).start()
    emit("ready", device=device, host=args.host, port=args.port, idle_timeout_seconds=idle_timeout_seconds)
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

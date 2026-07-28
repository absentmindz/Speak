"""Unified CLI entry point for Speak Python workers.

Usage:
    python speak_worker.py whisper --port 39731
    python speak_worker.py tts say --text "Hello" --out output.wav --model /path/to/model
    python speak_worker.py tts serve --port 8766 --model /path/to/model
    python speak_worker.py tts clone --text "Hello" --ref-audio ref.wav --out output.wav --model /path/to/model
"""

from __future__ import annotations

import argparse
import sys


def add_common_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0)
    parser.add_argument("--auth-token", default=None, help=argparse.SUPPRESS)


def build_whisper_parser(sub: argparse._SubParsersAction) -> None:
    p = sub.add_parser("whisper", help="Run the Whisper STT server")
    add_common_args(p)
    p.add_argument("--idle-minutes", type=float, default=10)
    p.add_argument("--model-dir", default="")
    p.add_argument("--max-audio-megabytes", type=int, default=512)
    p.set_defaults(command="whisper")


def build_tts_parser(sub: argparse._SubParsersAction) -> None:
    tts = sub.add_parser("tts", help="TTS operations")
    tts_sub = tts.add_subparsers(dest="tts_command")

    serve = tts_sub.add_parser("serve", help="Run the TTS server")
    add_common_args(serve)
    serve.add_argument("--model", required=True)
    serve.add_argument("--device", default="auto")
    serve.add_argument("--startup-timeout-seconds", type=int, default=600)
    serve.add_argument("--idle-minutes", type=float, default=10)
    serve.add_argument("--max-reference-audio-megabytes", type=int, default=512)
    serve.set_defaults(tts_command="serve")

    say = tts_sub.add_parser("say", help="Generate speech from text")
    say.add_argument("text")
    say.add_argument("--out", required=True)
    say.add_argument("--speaker", default="Aiden")
    say.add_argument("--language", default="English")
    say.add_argument("--model", required=True)
    say.add_argument("--device", default="auto")
    say.add_argument("--instruct", default="")
    say.add_argument("--max-new-tokens", type=int, default=None)
    say.set_defaults(tts_command="say")

    clone = tts_sub.add_parser("clone", help="Clone voice from reference audio")
    clone.add_argument("text")
    clone.add_argument("--ref-audio", required=True)
    clone.add_argument("--ref-text", default="")
    clone.add_argument("--out", required=True)
    clone.add_argument("--language", default="Auto")
    clone.add_argument("--model", required=True)
    clone.add_argument("--device", default="auto")
    clone.add_argument("--x-vector-only", action="store_true")
    clone.add_argument("--max-new-tokens", type=int, default=None)
    clone.set_defaults(tts_command="clone")


def main() -> int:
    parser = argparse.ArgumentParser(description="Speak Python worker.")
    sub = parser.add_subparsers(dest="command")
    sub.required = True

    build_whisper_parser(sub)
    build_tts_parser(sub)

    args = parser.parse_args()

    if args.command == "whisper":
        from whisper_resident_server import main as whisper_main

        sys.argv = [sys.argv[0]]
        sys.argv.extend(["--host", str(args.host)])
        sys.argv.extend(["--port", str(args.port or 39731)])
        sys.argv.extend(["--idle-minutes", str(args.idle_minutes)])
        if args.model_dir:
            sys.argv.extend(["--model-dir", args.model_dir])
        sys.argv.extend(["--max-audio-megabytes", str(args.max_audio_megabytes)])
        if args.auth_token:
            sys.argv.extend(["--auth-token", args.auth_token])
        whisper_main()
        return 0

    if args.tts_command == "serve":
        from qwen3_tts.qwen3_tts_worker import main as tts_server_main

        sys.argv = [sys.argv[0]]
        sys.argv.extend(["--host", str(args.host)])
        sys.argv.extend(["--port", str(args.port or 8766)])
        sys.argv.extend(["--model", args.model])
        sys.argv.extend(["--device", args.device])
        sys.argv.extend(["--startup-timeout-seconds", str(args.startup_timeout_seconds)])
        sys.argv.extend(["--idle-minutes", str(args.idle_minutes)])
        sys.argv.extend(["--max-reference-audio-megabytes", str(args.max_reference_audio_megabytes)])
        if args.auth_token:
            sys.argv.extend(["--auth-token", args.auth_token])
        return tts_server_main()

    if args.tts_command == "say":
        from qwen3_tts_local import main as tts_say_main

        sys.argv = [sys.argv[0], args.text]
        sys.argv.extend(["--out", args.out])
        sys.argv.extend(["--speaker", args.speaker])
        sys.argv.extend(["--language", args.language])
        sys.argv.extend(["--model", args.model])
        sys.argv.extend(["--device", args.device])
        sys.argv.extend(["--instruct", args.instruct])
        if args.max_new_tokens:
            sys.argv.extend(["--max-new-tokens", str(args.max_new_tokens)])
        return tts_say_main()

    if args.tts_command == "clone":
        from qwen3_tts_clone import main as tts_clone_main

        sys.argv = [sys.argv[0], args.text]
        sys.argv.extend(["--ref-audio", args.ref_audio])
        sys.argv.extend(["--ref-text", args.ref_text])
        sys.argv.extend(["--out", args.out])
        sys.argv.extend(["--language", args.language])
        sys.argv.extend(["--model", args.model])
        sys.argv.extend(["--device", args.device])
        if args.x_vector_only:
            sys.argv.append("--x-vector-only")
        if args.max_new_tokens:
            sys.argv.extend(["--max-new-tokens", str(args.max_new_tokens)])
        return tts_clone_main()

    return 1


if __name__ == "__main__":
    raise SystemExit(main())

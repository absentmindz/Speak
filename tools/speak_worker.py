"""Unified CLI entry point for Speak Python workers.

The wrapper keeps a stable public command line while delegating to the existing
worker implementations:

    python speak_worker.py whisper --port 39731
    python speak_worker.py tts serve --port 8766 --model /path/to/model
    python speak_worker.py tts say --text "Hello" --out output.wav --model /path/to/model
    python speak_worker.py tts clone --text "Hello" --ref-audio ref.wav --out output.wav --model /path/to/model

For backward compatibility, ``tts say`` and ``tts clone`` also accept text as
the first positional argument.
"""

from __future__ import annotations

import argparse
import importlib
import importlib.util
import inspect
import sys
from contextlib import contextmanager
from pathlib import Path
from types import ModuleType
from typing import Callable, Iterator, Sequence

from worker_security import ensure_loopback_host


TOOLS_ROOT = Path(__file__).resolve().parent
TargetMain = Callable[..., int | None]


def add_common_args(parser: argparse.ArgumentParser, *, default_port: int) -> None:
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=default_port)
    parser.add_argument("--auth-token", default=None, help=argparse.SUPPRESS)


def build_whisper_parser(sub: argparse._SubParsersAction) -> None:
    parser = sub.add_parser("whisper", help="Run the Whisper STT server")
    add_common_args(parser, default_port=39731)
    parser.add_argument("--idle-minutes", type=float, default=10)
    parser.add_argument("--model-dir", default="")
    parser.add_argument("--max-audio-megabytes", type=int, default=512)


def add_text_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("text", nargs="?", help=argparse.SUPPRESS)
    parser.add_argument("--text", dest="text_flag", help="Text to synthesize")


def build_tts_parser(sub: argparse._SubParsersAction) -> None:
    tts = sub.add_parser("tts", help="TTS operations")
    tts_sub = tts.add_subparsers(dest="tts_command", required=True)

    serve = tts_sub.add_parser("serve", help="Run the TTS server")
    add_common_args(serve, default_port=8766)
    serve.add_argument("--model", required=True)
    serve.add_argument("--device", default="auto")
    serve.add_argument("--startup-timeout-seconds", type=int, default=600)
    serve.add_argument("--idle-minutes", type=float, default=10)
    serve.add_argument("--max-reference-audio-megabytes", type=int, default=512)

    say = tts_sub.add_parser("say", help="Generate speech from text")
    add_text_args(say)
    say.add_argument("--out", required=True)
    say.add_argument("--speaker", default="Aiden")
    say.add_argument("--language", default="English")
    say.add_argument("--model", required=True)
    say.add_argument("--device", default="auto")
    say.add_argument("--instruct", default="")
    say.add_argument("--max-new-tokens", type=int, default=None)

    clone = tts_sub.add_parser("clone", help="Clone voice from reference audio")
    add_text_args(clone)
    clone.add_argument("--ref-audio", required=True)
    clone.add_argument("--ref-text", default="")
    clone.add_argument("--out", required=True)
    clone.add_argument("--language", default="Auto")
    clone.add_argument("--model", required=True)
    clone.add_argument("--device", default="auto")
    clone.add_argument("--x-vector-only", action="store_true")
    clone.add_argument("--max-new-tokens", type=int, default=None)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Speak Python worker.")
    sub = parser.add_subparsers(dest="command", required=True)
    build_whisper_parser(sub)
    build_tts_parser(sub)
    return parser


def resolve_text(parser: argparse.ArgumentParser, args: argparse.Namespace) -> str:
    positional = args.text
    flagged = args.text_flag
    if positional is not None and flagged is not None:
        parser.error("text may be supplied either positionally or with --text, not both")
    text = flagged if flagged is not None else positional
    if text is None:
        parser.error("the following argument is required: --text")
    return text


def load_path_module(module_name: str, path: Path) -> ModuleType:
    """Load a worker from a file path, including the upstream hyphenated folder."""

    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None or spec.loader is None:
        raise ImportError(f"Could not load worker module from {path}")
    module = importlib.util.module_from_spec(spec)
    previous = sys.modules.get(module_name)
    sys.modules[module_name] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        if previous is None:
            sys.modules.pop(module_name, None)
        else:
            sys.modules[module_name] = previous
        raise
    return module


def load_target_main(target: str) -> TargetMain:
    if target == "whisper":
        return importlib.import_module("whisper_resident_server").main
    if target == "tts-serve":
        # qwen3-tts is an upstream directory name and is not importable as the
        # invalid Python package name qwen3_tts. Load its worker by file path.
        module = load_path_module(
            "_speak_qwen3_tts_worker",
            TOOLS_ROOT / "qwen3-tts" / "qwen3_tts_worker.py",
        )
        return module.main
    if target == "tts-say":
        return importlib.import_module("qwen3_tts_local").main
    if target == "tts-clone":
        return importlib.import_module("qwen3_tts_clone").main
    raise ValueError(f"Unknown worker target: {target}")


@contextmanager
def temporary_argv(arguments: Sequence[str]) -> Iterator[None]:
    original = sys.argv
    sys.argv = [str(TOOLS_ROOT / "speak_worker.py"), *arguments]
    try:
        yield
    finally:
        sys.argv = original


def invoke_target(target: str, arguments: list[str]) -> int:
    target_main = load_target_main(target)
    try:
        parameters = inspect.signature(target_main).parameters
    except (TypeError, ValueError):
        parameters = {}

    argv_parameter = parameters.get("argv")
    if argv_parameter is not None:
        if argv_parameter.kind is inspect.Parameter.KEYWORD_ONLY:
            result = target_main(argv=arguments)
        else:
            result = target_main(arguments)
    else:
        # Legacy workers parse process-global argv. Keep compatibility while
        # restoring it even when parsing or worker startup fails.
        with temporary_argv(arguments):
            result = target_main()
    return 0 if result is None else int(result)


def append_auth_token(arguments: list[str], auth_token: str | None) -> None:
    if auth_token:
        arguments.extend(["--auth-token", auth_token])


def main(argv: Sequence[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    if args.command == "whisper":
        host = ensure_loopback_host(args.host)
        forwarded = [
            "--host",
            host,
            "--port",
            str(args.port),
            "--idle-minutes",
            str(args.idle_minutes),
        ]
        if args.model_dir:
            forwarded.extend(["--model-dir", args.model_dir])
        forwarded.extend(["--max-audio-megabytes", str(args.max_audio_megabytes)])
        append_auth_token(forwarded, args.auth_token)
        return invoke_target("whisper", forwarded)

    if args.tts_command == "serve":
        host = ensure_loopback_host(args.host)
        forwarded = [
            "--host",
            host,
            "--port",
            str(args.port),
            "--model",
            args.model,
            "--device",
            args.device,
            "--startup-timeout-seconds",
            str(args.startup_timeout_seconds),
            "--idle-minutes",
            str(args.idle_minutes),
            "--max-reference-audio-megabytes",
            str(args.max_reference_audio_megabytes),
        ]
        append_auth_token(forwarded, args.auth_token)
        return invoke_target("tts-serve", forwarded)

    if args.tts_command == "say":
        text = resolve_text(parser, args)
        forwarded = [
            text,
            "--out",
            args.out,
            "--speaker",
            args.speaker,
            "--language",
            args.language,
            "--model",
            args.model,
            "--device",
            args.device,
            "--instruct",
            args.instruct,
        ]
        if args.max_new_tokens is not None:
            forwarded.extend(["--max-new-tokens", str(args.max_new_tokens)])
        return invoke_target("tts-say", forwarded)

    if args.tts_command == "clone":
        text = resolve_text(parser, args)
        forwarded = [
            text,
            "--ref-audio",
            args.ref_audio,
            "--ref-text",
            args.ref_text,
            "--out",
            args.out,
            "--language",
            args.language,
            "--model",
            args.model,
            "--device",
            args.device,
        ]
        if args.x_vector_only:
            forwarded.append("--x-vector-only")
        if args.max_new_tokens is not None:
            forwarded.extend(["--max-new-tokens", str(args.max_new_tokens)])
        return invoke_target("tts-clone", forwarded)

    parser.error("unknown command")
    return 2


if __name__ == "__main__":
    raise SystemExit(main())

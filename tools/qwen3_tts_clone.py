import argparse

from qwen_tts_runtime import canonical_choice, emit, load_qwen_model, write_wav
from worker_security import public_error_code, validate_file, validate_text


MAX_TEXT_CHARS = 20_000
MAX_REFERENCE_AUDIO_BYTES = 512 * 1024 * 1024
SUPPORTED_AUDIO_SUFFIXES = {".aac", ".flac", ".m4a", ".mp3", ".ogg", ".opus", ".wav", ".webm"}


def parse_args():
    parser = argparse.ArgumentParser(description="Generate speech with a local Qwen3 Base voice-clone model.")
    parser.add_argument("text")
    parser.add_argument("--ref-audio", required=True)
    parser.add_argument("--ref-text", default="")
    parser.add_argument("--out", required=True)
    parser.add_argument("--language", default="Auto")
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--x-vector-only", action="store_true")
    parser.add_argument("--max-new-tokens", type=int, default=None)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    text = validate_text(args.text, field="text", maximum_chars=MAX_TEXT_CHARS)
    ref_audio = validate_file(
        args.ref_audio,
        field="ref-audio",
        maximum_bytes=MAX_REFERENCE_AUDIO_BYTES,
        allowed_suffixes=SUPPORTED_AUDIO_SUFFIXES,
    )
    ref_text = validate_text(
        args.ref_text,
        field="ref-text",
        maximum_chars=MAX_TEXT_CHARS,
        allow_empty=True,
    ) or None
    if args.max_new_tokens is not None and not 1 <= args.max_new_tokens <= 32_768:
        raise ValueError("max-new-tokens is outside the supported range.")
    model, device = load_qwen_model(args.model, args.device)
    supported_languages = model.model.get_supported_languages() if callable(getattr(model.model, "get_supported_languages", None)) else None
    language = canonical_choice(args.language, supported_languages, "Auto")
    x_vector_only = args.x_vector_only or ref_text is None
    kwargs = {}
    if args.max_new_tokens:
        kwargs["max_new_tokens"] = args.max_new_tokens
    wavs, sample_rate = model.generate_voice_clone(
        text=text,
        language=language,
        ref_audio=str(ref_audio),
        ref_text=ref_text,
        x_vector_only_mode=x_vector_only,
        **kwargs,
    )
    if not wavs:
        raise RuntimeError("The TTS model returned no audio.")
    output = write_wav(args.out, wavs[0], sample_rate)
    emit("generated", output=output, device=device, language=language, sample_rate=sample_rate, x_vector_only=x_vector_only)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        emit("error", code=public_error_code(exc))
        raise SystemExit(1) from None

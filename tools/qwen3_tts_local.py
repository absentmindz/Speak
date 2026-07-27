import argparse

from qwen_tts_runtime import canonical_choice, emit, load_qwen_model, write_wav
from worker_security import public_error_code, validate_text


MAX_TEXT_CHARS = 20_000
MAX_INSTRUCTION_CHARS = 4_000


def parse_args():
    parser = argparse.ArgumentParser(description="Generate speech with a local Qwen3 CustomVoice model.")
    parser.add_argument("text")
    parser.add_argument("--out", required=True)
    parser.add_argument("--speaker", default="Aiden")
    parser.add_argument("--language", default="English")
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--instruct", default="")
    parser.add_argument("--max-new-tokens", type=int, default=None)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    text = validate_text(args.text, field="text", maximum_chars=MAX_TEXT_CHARS)
    instruct = validate_text(
        args.instruct,
        field="instruct",
        maximum_chars=MAX_INSTRUCTION_CHARS,
        allow_empty=True,
    )
    if args.max_new_tokens is not None and not 1 <= args.max_new_tokens <= 32_768:
        raise ValueError("max-new-tokens is outside the supported range.")
    model, device = load_qwen_model(args.model, args.device)
    supported_speakers = model.model.get_supported_speakers() if callable(getattr(model.model, "get_supported_speakers", None)) else None
    supported_languages = model.model.get_supported_languages() if callable(getattr(model.model, "get_supported_languages", None)) else None
    speaker = canonical_choice(args.speaker, supported_speakers, "Aiden")
    language = canonical_choice(args.language, supported_languages, "English")
    kwargs = {}
    if args.max_new_tokens:
        kwargs["max_new_tokens"] = args.max_new_tokens
    wavs, sample_rate = model.generate_custom_voice(
        text=text,
        language=language,
        speaker=speaker,
        instruct=instruct or None,
        **kwargs,
    )
    if not wavs:
        raise RuntimeError("The TTS model returned no audio.")
    output = write_wav(args.out, wavs[0], sample_rate)
    emit("generated", output=output, device=device, speaker=speaker, language=language, sample_rate=sample_rate)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001
        emit("error", code=public_error_code(exc))
        raise SystemExit(1) from None

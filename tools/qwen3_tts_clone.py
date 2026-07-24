import argparse

from qwen_tts_runtime import canonical_choice, emit, load_qwen_model, write_wav


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
    model, device = load_qwen_model(args.model, args.device)
    supported_languages = model.model.get_supported_languages() if callable(getattr(model.model, "get_supported_languages", None)) else None
    language = canonical_choice(args.language, supported_languages, "Auto")
    ref_text = args.ref_text.strip() or None
    x_vector_only = args.x_vector_only or ref_text is None
    kwargs = {}
    if args.max_new_tokens:
        kwargs["max_new_tokens"] = args.max_new_tokens
    wavs, sample_rate = model.generate_voice_clone(
        text=args.text,
        language=language,
        ref_audio=args.ref_audio,
        ref_text=ref_text,
        x_vector_only_mode=x_vector_only,
        **kwargs,
    )
    output = write_wav(args.out, wavs[0], sample_rate)
    emit("generated", output=output, device=device, language=language, sample_rate=sample_rate, x_vector_only=x_vector_only)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

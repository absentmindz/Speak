import json
import os
import secrets
from pathlib import Path
from typing import Iterable, Optional

os.environ.setdefault("PYTHONUTF8", "1")
os.environ.setdefault("PYTHONIOENCODING", "utf-8")
os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "max_split_size_mb:128")
# Speak supports local model directories only. Force offline loading before
# importing Transformers/Qwen so a model configuration cannot fetch code.
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"

import soundfile as sf
import torch
from qwen_tts import Qwen3TTSModel

from worker_security import validate_local_model_directory, validate_output_wav


def emit(event: str, **values) -> None:
    print(json.dumps({"event": event, **values}, ensure_ascii=True), flush=True)


def dtype_for_device(device: str):
    if not str(device).startswith("cuda"):
        return torch.float32
    try:
        major, _ = torch.cuda.get_device_capability(0)
        return torch.bfloat16 if major >= 8 else torch.float16
    except Exception:
        return torch.float16


def load_qwen_model(model_path: str, requested_device: str = "auto") -> tuple[Qwen3TTSModel, str]:
    model_path = str(validate_local_model_directory(model_path))
    attempts = []
    requested = (requested_device or "auto").strip().lower()
    model_label = Path(model_path).name or "local-model"

    if requested in ("auto", "cuda", "cuda:0") and torch.cuda.is_available():
        attempts.append(("cuda:0", dtype_for_device("cuda:0"), {"device_map": "cuda:0"}))
        attempts.append(("auto", dtype_for_device("cuda:0"), {"device_map": "auto", "max_memory": {0: "4GiB", "cpu": "48GiB"}}))

    if requested not in ("auto", "cuda", "cuda:0", "cpu"):
        attempts.append((requested_device, dtype_for_device(requested_device), {"device_map": requested_device}))

    attempts.append(("cpu", torch.float32, {"device_map": "cpu"}))
    errors: list[str] = []

    for label, dtype, placement in attempts:
        kwargs = dict(placement)
        kwargs["dtype"] = dtype
        kwargs["low_cpu_mem_usage"] = True
        kwargs["attn_implementation"] = "eager"
        try:
            emit("loading", model=model_label, device=label, dtype=str(dtype).replace("torch.", ""))
            model = _from_pretrained(model_path, kwargs)
            emit("loaded", model=model_label, device=label)
            return model, label
        except Exception as exc:
            errors.append(f"{label}:{type(exc).__name__}")
            if "out of memory" in str(exc).lower():
                try:
                    torch.cuda.empty_cache()
                except Exception:
                    pass

    raise RuntimeError("Could not load Qwen TTS model (" + ", ".join(errors) + ").")


def _from_pretrained(model_path: str, kwargs: dict) -> Qwen3TTSModel:
    try:
        return Qwen3TTSModel.from_pretrained(model_path, **kwargs)
    except TypeError:
        retry = dict(kwargs)
        if "dtype" in retry:
            retry["torch_dtype"] = retry.pop("dtype")
        retry.pop("attn_implementation", None)
        return Qwen3TTSModel.from_pretrained(model_path, **retry)


def canonical_choice(value: Optional[str], supported: Optional[Iterable[str]], fallback: str) -> str:
    text = (value or "").strip()
    if not text:
        return fallback
    if supported:
        by_lower = {str(item).lower(): str(item) for item in supported}
        match = by_lower.get(text.lower())
        if match:
            return match
    return text


def write_wav(path: str, wav, sample_rate: int) -> str:
    output = validate_output_wav(path)
    if not isinstance(sample_rate, int) or not 8_000 <= sample_rate <= 384_000:
        raise ValueError("The generated audio sample rate is invalid.")
    output.parent.mkdir(parents=True, exist_ok=True)
    try:
        reservation = os.open(str(output), os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError as exc:
        raise FileExistsError("The requested output file already exists.") from exc
    else:
        os.close(reservation)

    temporary = output.with_name(f".{output.name}.{secrets.token_hex(8)}.tmp.wav")
    try:
        sf.write(str(temporary), wav, sample_rate)
        if not temporary.is_file() or temporary.stat().st_size <= 0:
            raise RuntimeError("The generated audio file is empty.")
        os.replace(temporary, output)
    except Exception:
        try:
            temporary.unlink(missing_ok=True)
        finally:
            output.unlink(missing_ok=True)
        raise
    return str(output)

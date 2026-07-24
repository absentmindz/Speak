import json
import os
from pathlib import Path
from typing import Iterable, Optional

os.environ.setdefault("PYTHONUTF8", "1")
os.environ.setdefault("PYTHONIOENCODING", "utf-8")
os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "max_split_size_mb:128")

import soundfile as sf
import torch
from qwen_tts import Qwen3TTSModel


def emit(event: str, **values) -> None:
    print(json.dumps({"event": event, **values}, ensure_ascii=True), flush=True)


def local_model_mode(model_path: str) -> None:
    if Path(model_path).exists():
        os.environ.setdefault("HF_HUB_OFFLINE", "1")
        os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")


def dtype_for_device(device: str):
    if not str(device).startswith("cuda"):
        return torch.float32
    try:
        major, _ = torch.cuda.get_device_capability(0)
        return torch.bfloat16 if major >= 8 else torch.float16
    except Exception:
        return torch.float16


def load_qwen_model(model_path: str, requested_device: str = "auto") -> tuple[Qwen3TTSModel, str]:
    local_model_mode(model_path)
    attempts = []
    requested = (requested_device or "auto").strip().lower()

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
            emit("loading", model=model_path, device=label, dtype=str(dtype).replace("torch.", ""))
            model = _from_pretrained(model_path, kwargs)
            emit("loaded", model=model_path, device=label)
            return model, label
        except Exception as exc:
            errors.append(f"{label}: {type(exc).__name__}: {exc}")
            if "out of memory" in str(exc).lower():
                try:
                    torch.cuda.empty_cache()
                except Exception:
                    pass

    raise RuntimeError("Could not load Qwen TTS model. " + " | ".join(errors))


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
    output = Path(path)
    output.parent.mkdir(parents=True, exist_ok=True)
    sf.write(str(output), wav, sample_rate)
    return str(output)

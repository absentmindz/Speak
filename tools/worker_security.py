"""Shared safety helpers for Speak's loopback-only Python workers.

This module intentionally uses only the Python standard library so its
security-sensitive behavior can be tested without loading ML frameworks.
"""

from __future__ import annotations

import hmac
import ipaddress
import json
import os
from pathlib import Path
from typing import Mapping, Optional


AUTH_ENV_VAR = "SPEAK_WORKER_TOKEN"
MIN_TOKEN_LENGTH = 32
MAX_PATH_CHARS = 32_767
MAX_MODEL_CONFIG_BYTES = 2 * 1024 * 1024
BLOCKED_MODEL_SUFFIXES = {
    ".bat",
    ".bin",
    ".ckpt",
    ".cmd",
    ".dll",
    ".dylib",
    ".exe",
    ".joblib",
    ".pickle",
    ".pkl",
    ".ps1",
    ".pt",
    ".pth",
    ".py",
    ".pyc",
    ".pyd",
    ".pyo",
    ".so",
}
BLOCKED_MODEL_CONFIG_KEYS = {
    "_attn_implementation_internal",
    "auto_map",
    "custom_pipelines",
    "trust_remote_code",
}


class RequestRejected(ValueError):
    """An expected request validation failure with an HTTP status."""

    def __init__(self, status: int, public_message: str):
        super().__init__(public_message)
        self.status = status
        self.public_message = public_message


def ensure_loopback_host(host: str) -> str:
    """Return a normalized loopback bind host or reject remote exposure."""

    candidate = (host or "").strip().lower().rstrip(".")
    if candidate == "localhost":
        return candidate
    try:
        address = ipaddress.ip_address(candidate)
    except ValueError as exc:
        raise ValueError("Worker host must be localhost or a loopback IP address.") from exc
    if not address.is_loopback:
        raise ValueError("Worker host must be a loopback IP address.")
    return candidate


def resolve_auth_token(explicit_token: Optional[str], environ: Optional[Mapping[str, str]] = None) -> str:
    """Resolve and validate the mandatory bearer token without logging it."""

    values = os.environ if environ is None else environ
    token = explicit_token if explicit_token is not None else values.get(AUTH_ENV_VAR, "")
    token = token.strip()
    if not token:
        raise ValueError(f"{AUTH_ENV_VAR} is required.")
    if len(token) < MIN_TOKEN_LENGTH:
        raise ValueError(f"Worker auth token must contain at least {MIN_TOKEN_LENGTH} characters.")
    if any(ch.isspace() or ord(ch) < 0x21 or ord(ch) > 0x7E for ch in token):
        raise ValueError("Worker auth token must contain printable non-whitespace ASCII characters only.")
    return token


def bearer_token_from_header(header_value: Optional[str]) -> str:
    if not header_value:
        return ""
    scheme, separator, credentials = header_value.partition(" ")
    if not separator or scheme.casefold() != "bearer":
        return ""
    return credentials.strip()


def is_authorized(header_value: Optional[str], expected_token: str) -> bool:
    """Validate a mandatory bearer token with constant-time comparison."""

    if not expected_token:
        return False
    supplied = bearer_token_from_header(header_value)
    return bool(supplied) and hmac.compare_digest(supplied, expected_token)


def is_loopback_host_header(header_value: Optional[str]) -> bool:
    """Accept only Host headers that name the loopback listener."""

    if not header_value:
        return False
    value = header_value.strip()
    if not value or any(ch in value for ch in ("@", "/", "\\", ",", "\r", "\n", "\t", " ")):
        return False

    if value.startswith("["):
        end = value.find("]")
        if end < 0:
            return False
        host = value[1:end]
        suffix = value[end + 1 :]
        if suffix and (not suffix.startswith(":") or not suffix[1:].isdigit()):
            return False
    else:
        host = value
        if value.count(":") == 1:
            possible_host, possible_port = value.rsplit(":", 1)
            if not possible_port.isdigit():
                return False
            host = possible_host

    try:
        ensure_loopback_host(host)
        return True
    except ValueError:
        return False


def require_json_content_type(header_value: Optional[str]) -> None:
    media_type = (header_value or "").partition(";")[0].strip().casefold()
    if media_type != "application/json":
        raise RequestRejected(415, "Content-Type must be application/json.")


def checked_content_length(
    header_value: Optional[str],
    *,
    maximum_bytes: int,
    require_body: bool = True,
) -> int:
    if not header_value:
        if require_body:
            raise RequestRejected(411, "Content-Length is required.")
        return 0
    try:
        length = int(header_value, 10)
    except (TypeError, ValueError) as exc:
        raise RequestRejected(400, "Content-Length is invalid.") from exc
    if length < 0:
        raise RequestRejected(400, "Content-Length is invalid.")
    if length > maximum_bytes:
        raise RequestRejected(413, "Request body is too large.")
    return length


def validate_text(value: object, *, field: str, maximum_chars: int, allow_empty: bool = False) -> str:
    if not isinstance(value, str):
        raise RequestRejected(400, f"{field} must be text.")
    text = value.strip()
    if not text and not allow_empty:
        raise RequestRejected(400, f"{field} is required.")
    if len(text) > maximum_chars:
        raise RequestRejected(413, f"{field} is too long.")
    return text


def validate_file(
    value: object,
    *,
    field: str,
    maximum_bytes: int,
    allowed_suffixes: Optional[set[str]] = None,
) -> Path:
    if isinstance(value, os.PathLike):
        value = os.fspath(value)
    text = validate_text(value, field=field, maximum_chars=MAX_PATH_CHARS)
    path = Path(text)
    try:
        if not path.is_file():
            raise RequestRejected(400, f"{field} is not a readable file.")
        size = path.stat().st_size
    except OSError as exc:
        raise RequestRejected(400, f"{field} is not a readable file.") from exc
    if size <= 0:
        raise RequestRejected(400, f"{field} is empty.")
    if size > maximum_bytes:
        raise RequestRejected(413, f"{field} is too large.")
    if allowed_suffixes and path.suffix.casefold() not in {suffix.casefold() for suffix in allowed_suffixes}:
        raise RequestRejected(400, f"{field} has an unsupported file type.")
    return path


def validate_local_model_directory(value: object) -> Path:
    """Require a local, data-only model tree before loading ML code.

    Speak does not support remote model identifiers. Reject executable or
    pickle-based files and configuration hooks that can download or execute
    custom code through model-loading libraries.
    """

    if isinstance(value, os.PathLike):
        value = os.fspath(value)
    text = validate_text(value, field="model", maximum_chars=MAX_PATH_CHARS)
    try:
        root = Path(text).expanduser().resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise ValueError("The model directory is not available.") from exc
    if not root.is_dir():
        raise ValueError("The model path must be a local directory.")

    for path in root.rglob("*"):
        if path.is_symlink():
            raise ValueError("Model directories must not contain symbolic links.")
        if path.is_file() and path.suffix.casefold() in BLOCKED_MODEL_SUFFIXES:
            raise ValueError("Model directories must contain data-only safe tensor files.")

    for config_path in root.rglob("config.json"):
        try:
            if config_path.stat().st_size > MAX_MODEL_CONFIG_BYTES:
                raise ValueError("A model configuration file is too large.")
            with config_path.open("r", encoding="utf-8") as stream:
                config = json.load(stream)
        except (OSError, UnicodeError, json.JSONDecodeError, RecursionError) as exc:
            raise ValueError("A model configuration file is invalid.") from exc
        blocked_key = _find_blocked_model_config_key(config)
        if blocked_key is not None:
            raise ValueError(
                f"Model configuration contains unsupported executable hook '{blocked_key}'."
            )

    return root


def _find_blocked_model_config_key(value: object) -> Optional[str]:
    pending = [value]
    while pending:
        current = pending.pop()
        if isinstance(current, dict):
            for key, child in current.items():
                if str(key).casefold() in BLOCKED_MODEL_CONFIG_KEYS:
                    return str(key)
                pending.append(child)
        elif isinstance(current, list):
            pending.extend(current)
    return None


def validate_output_wav(value: object) -> Path:
    if isinstance(value, os.PathLike):
        value = os.fspath(value)
    text = validate_text(value, field="output", maximum_chars=MAX_PATH_CHARS)
    path = Path(text)
    if path.suffix.casefold() != ".wav":
        raise RequestRejected(400, "output must use the .wav extension.")
    if path.name in {"", ".", ".."}:
        raise RequestRejected(400, "output is invalid.")
    return path


def public_error_code(exc: BaseException) -> str:
    """Map internal failures to a path/token-free diagnostic code."""

    name = type(exc).__name__.casefold()
    message = str(exc).casefold()
    if "out of memory" in message or "cuda" in message and "memory" in message:
        return "out_of_memory"
    if "timeout" in name or "timeout" in message:
        return "timeout"
    if "permission" in name:
        return "permission_denied"
    if "filenotfound" in name:
        return "file_not_found"
    return "worker_failure"

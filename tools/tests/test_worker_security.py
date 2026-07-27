from __future__ import annotations

import os
import sys
import tempfile
import unittest
from pathlib import Path


TOOLS_ROOT = Path(__file__).resolve().parents[1]
if str(TOOLS_ROOT) not in sys.path:
    sys.path.insert(0, str(TOOLS_ROOT))

from worker_security import (  # noqa: E402
    RequestRejected,
    checked_content_length,
    ensure_loopback_host,
    is_authorized,
    is_loopback_host_header,
    public_error_code,
    require_json_content_type,
    resolve_auth_token,
    validate_file,
    validate_local_model_directory,
    validate_output_wav,
    validate_text,
)


class WorkerSecurityTests(unittest.TestCase):
    def test_bind_host_must_be_loopback(self) -> None:
        self.assertEqual("127.0.0.1", ensure_loopback_host("127.0.0.1"))
        self.assertEqual("::1", ensure_loopback_host("::1"))
        self.assertEqual("localhost", ensure_loopback_host("LOCALHOST."))
        for host in ("0.0.0.0", "192.168.1.10", "example.com", ""):
            with self.subTest(host=host), self.assertRaises(ValueError):
                ensure_loopback_host(host)

    def test_host_header_must_be_loopback(self) -> None:
        accepted = ("127.0.0.1:39731", "localhost:8766", "[::1]:8766", "127.0.0.2")
        rejected = ("example.com", "127.0.0.1@example.com", "localhost/path", "", None)
        for value in accepted:
            with self.subTest(value=value):
                self.assertTrue(is_loopback_host_header(value))
        for value in rejected:
            with self.subTest(value=value):
                self.assertFalse(is_loopback_host_header(value))

    def test_token_resolution_and_constant_time_comparison_contract(self) -> None:
        token = "a" * 32
        self.assertEqual(token, resolve_auth_token(None, {"SPEAK_WORKER_TOKEN": token}))
        self.assertTrue(is_authorized(f"Bearer {token}", token))
        self.assertTrue(is_authorized(f"bearer {token}", token))
        self.assertFalse(is_authorized("Bearer wrong", token))
        self.assertFalse(is_authorized(None, token))
        self.assertFalse(is_authorized(None, ""))
        with self.assertRaises(ValueError):
            resolve_auth_token(None, {})
        with self.assertRaises(ValueError):
            resolve_auth_token("too-short", {})

    def test_content_type_and_length_limits(self) -> None:
        require_json_content_type("application/json; charset=utf-8")
        with self.assertRaises(RequestRejected) as content_error:
            require_json_content_type("text/plain")
        self.assertEqual(415, content_error.exception.status)
        self.assertEqual(10, checked_content_length("10", maximum_bytes=10))
        with self.assertRaises(RequestRejected) as size_error:
            checked_content_length("11", maximum_bytes=10)
        self.assertEqual(413, size_error.exception.status)
        with self.assertRaises(RequestRejected) as missing_error:
            checked_content_length(None, maximum_bytes=10)
        self.assertEqual(411, missing_error.exception.status)

    def test_text_and_output_validation(self) -> None:
        self.assertEqual("hello", validate_text(" hello ", field="text", maximum_chars=10))
        with self.assertRaises(RequestRejected):
            validate_text("", field="text", maximum_chars=10)
        with self.assertRaises(RequestRejected):
            validate_text("x" * 11, field="text", maximum_chars=10)
        self.assertEqual(".wav", validate_output_wav("result.wav").suffix)
        with self.assertRaises(RequestRejected):
            validate_output_wav("result.mp3")

    def test_file_validation_checks_size_and_type_without_exposing_path(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            audio_path = Path(temp_dir) / "sample.wav"
            audio_path.write_bytes(b"RIFF")
            self.assertEqual(audio_path, validate_file(audio_path, field="audio", maximum_bytes=4))
            with self.assertRaises(RequestRejected) as too_large:
                validate_file(audio_path, field="audio", maximum_bytes=3)
            self.assertNotIn(temp_dir, too_large.exception.public_message)

    def test_local_model_directory_accepts_data_only_safe_tensors(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            model_root = Path(temp_dir) / "model"
            model_root.mkdir()
            (model_root / "config.json").write_text(
                '{"model_type":"qwen3_tts","nested":{"mode":"eager"}}',
                encoding="utf-8",
            )
            (model_root / "model.safetensors").write_bytes(b"safe")

            self.assertEqual(model_root.resolve(), validate_local_model_directory(model_root))

    def test_local_model_directory_rejects_executable_hooks_and_pickle_weights(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            model_root = Path(temp_dir) / "model"
            model_root.mkdir()
            (model_root / "config.json").write_text(
                '{"nested":{"auto_map":{"AutoModel":"remote.module"}}}',
                encoding="utf-8",
            )
            with self.assertRaises(ValueError):
                validate_local_model_directory(model_root)

            (model_root / "config.json").write_text("{}", encoding="utf-8")
            (model_root / "weights.pth").write_bytes(b"pickle")
            with self.assertRaises(ValueError):
                validate_local_model_directory(model_root)

    def test_public_error_code_does_not_echo_sensitive_error_text(self) -> None:
        secret_path = os.path.join("C:\\", "Users", "someone", "secret.wav")
        self.assertEqual("file_not_found", public_error_code(FileNotFoundError(secret_path)))
        self.assertNotIn("someone", public_error_code(RuntimeError(secret_path)))


if __name__ == "__main__":
    unittest.main()

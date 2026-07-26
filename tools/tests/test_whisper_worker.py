from __future__ import annotations

import http.client
import importlib.util
import json
import sys
import tempfile
import threading
import time
import types
import unittest
from pathlib import Path


TOOLS_ROOT = Path(__file__).resolve().parents[1]
if str(TOOLS_ROOT) not in sys.path:
    sys.path.insert(0, str(TOOLS_ROOT))


class FakeCuda:
    @staticmethod
    def is_available() -> bool:
        return False

    @staticmethod
    def synchronize() -> None:
        return

    @staticmethod
    def empty_cache() -> None:
        return


class FakeWhisperModel:
    def __init__(self) -> None:
        self.fail = False
        self.active = 0
        self.maximum_active = 0
        self.lock = threading.Lock()

    def transcribe(self, _path: str, **_kwargs):
        with self.lock:
            self.active += 1
            self.maximum_active = max(self.maximum_active, self.active)
        try:
            time.sleep(0.05)
            if self.fail:
                raise RuntimeError(r"C:\Sensitive\private-recording.wav")
            return {"text": "hello from the fake model"}
        finally:
            with self.lock:
                self.active -= 1


class WhisperWorkerHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.original_modules = {name: sys.modules.get(name) for name in ("torch", "whisper")}
        cls.fake_model = FakeWhisperModel()
        fake_torch = types.ModuleType("torch")
        fake_torch.cuda = FakeCuda()
        fake_torch.__version__ = "test"
        fake_whisper = types.ModuleType("whisper")
        fake_whisper.load_model = lambda *_args, **_kwargs: cls.fake_model
        sys.modules["torch"] = fake_torch
        sys.modules["whisper"] = fake_whisper

        module_path = TOOLS_ROOT / "whisper_resident_server.py"
        spec = importlib.util.spec_from_file_location("speak_test_whisper_worker", module_path)
        assert spec and spec.loader
        cls.worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.worker)

        cls.temp_dir = tempfile.TemporaryDirectory()
        cls.audio_path = Path(cls.temp_dir.name) / "sample.wav"
        cls.audio_path.write_bytes(b"RIFF")
        cls.token = "test-token-" + "a" * 32
        cls.worker.AUTH_TOKEN = cls.token
        cls.worker.STATE = cls.worker.WhisperState(10, cls.temp_dir.name, 1024)
        cls.server = cls.worker.WorkerHttpServer(("127.0.0.1", 0), cls.worker.Handler)
        cls.worker.SERVER = cls.server
        cls.port = cls.server.server_address[1]
        cls.server_thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.server_thread.start()

    @classmethod
    def tearDownClass(cls) -> None:
        cls.server.shutdown()
        cls.server.server_close()
        cls.server_thread.join(timeout=2)
        cls.temp_dir.cleanup()
        for name, previous in cls.original_modules.items():
            if previous is None:
                sys.modules.pop(name, None)
            else:
                sys.modules[name] = previous

    @classmethod
    def request(cls, method: str, path: str, payload=None, *, token: str | None = None, content_type: str = "application/json"):
        headers = {}
        body = None
        if token is not None:
            headers["Authorization"] = f"Bearer {token}"
        if payload is not None:
            body = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = content_type
            headers["Content-Length"] = str(len(body))
        connection = http.client.HTTPConnection("127.0.0.1", cls.port, timeout=3)
        try:
            connection.request(method, path, body=body, headers=headers)
            response = connection.getresponse()
            data = json.loads(response.read().decode("utf-8"))
            return response.status, data
        finally:
            connection.close()

    def transcribe_payload(self) -> dict[str, object]:
        return {
            "audioPath": str(self.audio_path),
            "model": "base",
            "modelDir": self.temp_dir.name,
            "device": "cpu",
            "keepAliveMinutes": 10,
            "language": "en",
        }

    def test_health_requires_bearer_token(self) -> None:
        status, _ = self.request("GET", "/health")
        self.assertEqual(401, status)
        status, body = self.request("GET", "/health", token=self.token)
        self.assertEqual(200, status)
        self.assertTrue(body["authenticationRequired"])

    def test_post_rejects_browser_simple_content_type(self) -> None:
        status, _ = self.request(
            "POST",
            "/transcribe",
            self.transcribe_payload(),
            token=self.token,
            content_type="text/plain",
        )
        self.assertEqual(415, status)

    def test_transcription_failure_does_not_leak_path_or_double_decrement(self) -> None:
        self.fake_model.fail = True
        try:
            status, body = self.request(
                "POST",
                "/transcribe",
                self.transcribe_payload(),
                token=self.token,
            )
        finally:
            self.fake_model.fail = False
        self.assertEqual(500, status)
        serialized = json.dumps(body)
        self.assertNotIn("Sensitive", serialized)
        self.assertNotIn("private-recording", serialized)

        deadline = time.monotonic() + 1.0
        while time.monotonic() < deadline:
            with self.worker.STATE.lock:
                if (
                    self.worker.STATE.active_transcriptions == 0
                    and self.worker.STATE.active_requests == 0
                ):
                    break
            time.sleep(0.01)
        with self.worker.STATE.lock:
            self.assertEqual(0, self.worker.STATE.active_transcriptions)
            self.assertEqual(0, self.worker.STATE.active_requests)

    def test_concurrent_transcriptions_are_serialized(self) -> None:
        statuses: list[int] = []

        def run_request() -> None:
            status, _ = self.request(
                "POST",
                "/transcribe",
                self.transcribe_payload(),
                token=self.token,
            )
            statuses.append(status)

        threads = [threading.Thread(target=run_request) for _ in range(2)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=3)
        self.assertEqual([200, 200], sorted(statuses))
        self.assertEqual(1, self.fake_model.maximum_active)


if __name__ == "__main__":
    unittest.main()

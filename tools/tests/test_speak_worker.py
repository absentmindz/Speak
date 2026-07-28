from __future__ import annotations

import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock


TOOLS_ROOT = Path(__file__).resolve().parents[1]
if str(TOOLS_ROOT) not in sys.path:
    sys.path.insert(0, str(TOOLS_ROOT))

import speak_worker  # noqa: E402


class SpeakWorkerDispatchTests(unittest.TestCase):
    def dispatch(self, argv: list[str], return_value: int | None = 0):
        calls: list[list[str]] = []
        original_argv = sys.argv

        def target_main() -> int | None:
            calls.append(sys.argv[1:].copy())
            return return_value

        with mock.patch.object(speak_worker, "load_target_main", return_value=target_main) as loader:
            result = speak_worker.main(argv)

        self.assertIs(sys.argv, original_argv)
        self.assertEqual(len(calls), 1)
        return result, loader.call_args.args[0], calls[0]

    def test_whisper_dispatch_forwards_all_arguments_and_restores_argv(self) -> None:
        result, target, forwarded = self.dispatch(
            [
                "whisper",
                "--host",
                "localhost",
                "--port",
                "41001",
                "--idle-minutes",
                "7.5",
                "--model-dir",
                "models/whisper",
                "--max-audio-megabytes",
                "128",
                "--auth-token",
                "x" * 32,
            ],
            return_value=None,
        )

        self.assertEqual(result, 0)
        self.assertEqual(target, "whisper")
        self.assertEqual(
            forwarded,
            [
                "--host",
                "localhost",
                "--port",
                "41001",
                "--idle-minutes",
                "7.5",
                "--model-dir",
                "models/whisper",
                "--max-audio-megabytes",
                "128",
                "--auth-token",
                "x" * 32,
            ],
        )

    def test_tts_serve_dispatch_forwards_all_arguments(self) -> None:
        result, target, forwarded = self.dispatch(
            [
                "tts",
                "serve",
                "--host",
                "127.0.0.2",
                "--port",
                "8767",
                "--model",
                "base-model",
                "--device",
                "cuda",
                "--startup-timeout-seconds",
                "300",
                "--idle-minutes",
                "15",
                "--max-reference-audio-megabytes",
                "64",
            ],
            return_value=9,
        )

        self.assertEqual(result, 9)
        self.assertEqual(target, "tts-serve")
        self.assertEqual(
            forwarded,
            [
                "--host",
                "127.0.0.2",
                "--port",
                "8767",
                "--model",
                "base-model",
                "--device",
                "cuda",
                "--startup-timeout-seconds",
                "300",
                "--idle-minutes",
                "15.0",
                "--max-reference-audio-megabytes",
                "64",
            ],
        )

    def test_tts_say_accepts_text_flag_and_forwards_zero_for_validation(self) -> None:
        result, target, forwarded = self.dispatch(
            [
                "tts",
                "say",
                "--text",
                "hello world",
                "--out",
                "out.wav",
                "--speaker",
                "Ryan",
                "--language",
                "English",
                "--model",
                "custom-model",
                "--device",
                "cpu",
                "--instruct",
                "calm",
                "--max-new-tokens",
                "0",
            ]
        )

        self.assertEqual(result, 0)
        self.assertEqual(target, "tts-say")
        self.assertEqual(
            forwarded,
            [
                "hello world",
                "--out",
                "out.wav",
                "--speaker",
                "Ryan",
                "--language",
                "English",
                "--model",
                "custom-model",
                "--device",
                "cpu",
                "--instruct",
                "calm",
                "--max-new-tokens",
                "0",
            ],
        )

    def test_tts_clone_accepts_legacy_positional_text(self) -> None:
        result, target, forwarded = self.dispatch(
            [
                "tts",
                "clone",
                "legacy text",
                "--ref-audio",
                "reference.wav",
                "--ref-text",
                "reference words",
                "--out",
                "clone.wav",
                "--language",
                "Auto",
                "--model",
                "base-model",
                "--device",
                "cuda",
                "--x-vector-only",
                "--max-new-tokens",
                "42",
            ]
        )

        self.assertEqual(result, 0)
        self.assertEqual(target, "tts-clone")
        self.assertEqual(
            forwarded,
            [
                "legacy text",
                "--ref-audio",
                "reference.wav",
                "--ref-text",
                "reference words",
                "--out",
                "clone.wav",
                "--language",
                "Auto",
                "--model",
                "base-model",
                "--device",
                "cuda",
                "--x-vector-only",
                "--max-new-tokens",
                "42",
            ],
        )

    def test_argv_aware_worker_does_not_require_global_argv_mutation(self) -> None:
        original_argv = sys.argv
        received: list[str] = []

        def target_main(argv=None):
            received.extend(argv)
            self.assertIs(sys.argv, original_argv)
            return 4

        with mock.patch.object(speak_worker, "load_target_main", return_value=target_main):
            result = speak_worker.main(
                ["tts", "say", "text", "--out", "out.wav", "--model", "model"]
            )

        self.assertEqual(result, 4)
        self.assertEqual(received[0], "text")

    def test_legacy_worker_failure_still_restores_global_argv(self) -> None:
        original_argv = sys.argv

        def target_main():
            raise RuntimeError("worker failed")

        with mock.patch.object(speak_worker, "load_target_main", return_value=target_main):
            with self.assertRaisesRegex(RuntimeError, "worker failed"):
                speak_worker.main(
                    ["tts", "say", "text", "--out", "out.wav", "--model", "model"]
                )

        self.assertIs(sys.argv, original_argv)

    def test_server_commands_reject_non_loopback_hosts_before_import(self) -> None:
        with mock.patch.object(speak_worker, "load_target_main") as loader:
            with self.assertRaisesRegex(ValueError, "loopback"):
                speak_worker.main(
                    ["tts", "serve", "--host", "0.0.0.0", "--model", "model"]
                )
        loader.assert_not_called()

    def test_top_level_and_tts_subcommands_are_required(self) -> None:
        for argv in ([], ["tts"]):
            with self.subTest(argv=argv), self.assertRaises(SystemExit) as raised:
                speak_worker.main(argv)
            self.assertEqual(raised.exception.code, 2)

    def test_text_is_required_and_cannot_be_supplied_twice(self) -> None:
        invalid_commands = (
            ["tts", "say", "--out", "out.wav", "--model", "model"],
            [
                "tts",
                "clone",
                "positional",
                "--text",
                "flagged",
                "--ref-audio",
                "ref.wav",
                "--out",
                "out.wav",
                "--model",
                "model",
            ],
        )
        for argv in invalid_commands:
            with self.subTest(argv=argv), self.assertRaises(SystemExit) as raised:
                speak_worker.main(argv)
            self.assertEqual(raised.exception.code, 2)

    def test_hyphenated_qwen_worker_is_loaded_from_its_file_path(self) -> None:
        target_main = mock.Mock(return_value=0)
        module = SimpleNamespace(main=target_main)
        loader = mock.Mock()
        spec = SimpleNamespace(loader=loader)

        with (
            mock.patch.object(speak_worker.importlib.util, "spec_from_file_location", return_value=spec) as make_spec,
            mock.patch.object(speak_worker.importlib.util, "module_from_spec", return_value=module),
            mock.patch.dict(sys.modules, {}, clear=False),
        ):
            loaded = speak_worker.load_target_main("tts-serve")
            self.assertIs(sys.modules["_speak_qwen3_tts_worker"], module)

        self.assertIs(loaded, target_main)
        loader.exec_module.assert_called_once_with(module)
        module_path = make_spec.call_args.args[1]
        self.assertEqual(module_path, TOOLS_ROOT / "qwen3-tts" / "qwen3_tts_worker.py")


if __name__ == "__main__":
    unittest.main()

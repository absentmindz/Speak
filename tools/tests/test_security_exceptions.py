from __future__ import annotations

import copy
import importlib.util
import json
import unittest
from datetime import date
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPO_ROOT / "tools" / "validate_security_exceptions.py"
SPEC = importlib.util.spec_from_file_location("validate_security_exceptions", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("Could not load security exception validator.")
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class SecurityExceptionRegistryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.registry = json.loads(
            (REPO_ROOT / "tools" / "security-exceptions.json").read_text(
                encoding="utf-8"
            )
        )
        cls.workflow = (
            REPO_ROOT / ".github" / "workflows" / "build.yml"
        ).read_text(encoding="utf-8")
        cls.security = (REPO_ROOT / "SECURITY.md").read_text(encoding="utf-8")

    def test_current_registry_is_valid(self) -> None:
        VALIDATOR.validate_registry(
            copy.deepcopy(self.registry),
            REPO_ROOT,
            self.workflow,
            self.security,
            today=date(2026, 7, 28),
        )

    def test_expired_exception_is_rejected(self) -> None:
        registry = copy.deepcopy(self.registry)
        registry["exceptions"][0]["expiresDate"] = "2026-07-27"
        with self.assertRaisesRegex(VALIDATOR.RegistryError, "expired"):
            VALIDATOR.validate_registry(
                registry,
                REPO_ROOT,
                self.workflow,
                self.security,
                today=date(2026, 7, 28),
            )

    def test_untracked_pip_audit_ignore_is_rejected(self) -> None:
        workflow = self.workflow + "\n# --ignore-vuln PYSEC-2099-999\n"
        with self.assertRaisesRegex(VALIDATOR.RegistryError, "ignore drift"):
            VALIDATOR.validate_registry(
                copy.deepcopy(self.registry),
                REPO_ROOT,
                workflow,
                self.security,
                today=date(2026, 7, 28),
            )

    def test_untracked_dependency_review_allow_is_rejected(self) -> None:
        workflow = self.workflow.replace(
            "allow-ghsas: GHSA-h35f-9h28-mq5c",
            "allow-ghsas: GHSA-h35f-9h28-mq5c, GHSA-xxxx-yyyy-zzzz",
        )
        with self.assertRaisesRegex(VALIDATOR.RegistryError, "allowlist drift"):
            VALIDATOR.validate_registry(
                copy.deepcopy(self.registry),
                REPO_ROOT,
                workflow,
                self.security,
                today=date(2026, 7, 28),
            )

    def test_lock_pin_drift_is_rejected(self) -> None:
        registry = copy.deepcopy(self.registry)
        registry["exceptions"][0]["pinnedVersion"] = "999.0.0"
        with self.assertRaisesRegex(VALIDATOR.RegistryError, "is not pinned"):
            VALIDATOR.validate_registry(
                registry,
                REPO_ROOT,
                self.workflow,
                self.security,
                today=date(2026, 7, 28),
            )


if __name__ == "__main__":
    unittest.main()

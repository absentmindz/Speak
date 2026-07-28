"""Validate Speak's temporary dependency security exception registry.

The registry is deliberately strict: CI fails when an exception expires, misses
its review cadence, stops matching the lock file, is not documented, or drifts
from the exact pip-audit ignore list in the build workflow.
"""

from __future__ import annotations

import json
import re
import sys
from datetime import date
from pathlib import Path
from typing import Any


ADVISORY_PATTERN = re.compile(
    r"^(?:PYSEC-\d{4}-\d+|CVE-\d{4}-\d+|GHSA-[0-9a-z-]+)$",
    re.IGNORECASE,
)
REQUIRED_FIELDS = {
    "id",
    "advisoryIds",
    "package",
    "pinnedVersion",
    "lockPath",
    "component",
    "severity",
    "owner",
    "remediationOwner",
    "technicalBlocker",
    "exploitabilityAssessment",
    "compensatingControls",
    "validationEvidence",
    "createdDate",
    "lastReviewedDate",
    "expiresDate",
    "reviewCadenceDays",
    "exitCriteria",
    "upstreamReferences",
    "enforcement",
    "status",
    "approval",
    "renewalHistory",
}
ALLOWED_SEVERITIES = {"low", "medium", "high", "critical"}
ALLOWED_ENFORCEMENT = {"pip-audit-ignore", "dependabot-open"}


class RegistryError(ValueError):
    """Raised when the exception registry is unsafe or internally inconsistent."""


def _require_text(entry: dict[str, Any], field: str, exception_id: str) -> str:
    value = entry.get(field)
    if not isinstance(value, str) or not value.strip():
        raise RegistryError(f"{exception_id}: {field} must be non-empty text.")
    return value.strip()


def _parse_date(value: str, field: str, exception_id: str) -> date:
    try:
        return date.fromisoformat(value)
    except ValueError as exc:
        raise RegistryError(
            f"{exception_id}: {field} must use YYYY-MM-DD format."
        ) from exc


def _require_text_list(
    entry: dict[str, Any], field: str, exception_id: str, *, allow_empty: bool = False
) -> list[str]:
    value = entry.get(field)
    if not isinstance(value, list) or (not allow_empty and not value):
        qualifier = "a list" if allow_empty else "a non-empty list"
        raise RegistryError(f"{exception_id}: {field} must be {qualifier}.")
    if any(not isinstance(item, str) or not item.strip() for item in value):
        raise RegistryError(f"{exception_id}: {field} contains an empty value.")
    return [item.strip() for item in value]


def validate_registry(
    registry: dict[str, Any],
    repo_root: Path,
    workflow_text: str,
    security_text: str,
    *,
    today: date | None = None,
) -> None:
    today = today or date.today()
    if registry.get("schemaVersion") != 1:
        raise RegistryError("security-exceptions.json schemaVersion must be 1.")
    if not isinstance(registry.get("registryOwner"), str) or not registry["registryOwner"].strip():
        raise RegistryError("security-exceptions.json requires registryOwner.")
    default_cadence = registry.get("defaultReviewCadenceDays")
    if not isinstance(default_cadence, int) or default_cadence < 1:
        raise RegistryError("defaultReviewCadenceDays must be a positive integer.")

    entries = registry.get("exceptions")
    if not isinstance(entries, list) or not entries:
        raise RegistryError("security-exceptions.json must contain exceptions.")

    seen_exception_ids: set[str] = set()
    seen_advisory_ids: set[str] = set()
    expected_pip_ignores: set[str] = set()
    expected_dependency_review_allows: set[str] = set()

    for raw_entry in entries:
        if not isinstance(raw_entry, dict):
            raise RegistryError("Every exception must be a JSON object.")
        missing = REQUIRED_FIELDS.difference(raw_entry)
        provisional_id = str(raw_entry.get("id", "<unknown>"))
        if missing:
            raise RegistryError(
                f"{provisional_id}: missing required fields: {', '.join(sorted(missing))}."
            )

        exception_id = _require_text(raw_entry, "id", provisional_id)
        if not re.fullmatch(r"SEC-\d{4}-\d{3}", exception_id):
            raise RegistryError(f"{exception_id}: id must match SEC-YYYY-NNN.")
        if exception_id in seen_exception_ids:
            raise RegistryError(f"Duplicate exception id: {exception_id}.")
        seen_exception_ids.add(exception_id)

        for field in (
            "package",
            "pinnedVersion",
            "lockPath",
            "component",
            "owner",
            "remediationOwner",
            "technicalBlocker",
            "exploitabilityAssessment",
            "exitCriteria",
            "enforcement",
            "status",
            "approval",
        ):
            _require_text(raw_entry, field, exception_id)

        severity = _require_text(raw_entry, "severity", exception_id).lower()
        if severity not in ALLOWED_SEVERITIES:
            raise RegistryError(f"{exception_id}: unsupported severity {severity!r}.")
        enforcement = _require_text(raw_entry, "enforcement", exception_id)
        if enforcement not in ALLOWED_ENFORCEMENT:
            raise RegistryError(f"{exception_id}: unsupported enforcement {enforcement!r}.")
        if raw_entry["status"] != "accepted-temporary":
            raise RegistryError(f"{exception_id}: status must be accepted-temporary.")

        advisories = _require_text_list(raw_entry, "advisoryIds", exception_id)
        dependency_review_allow = raw_entry.get("dependencyReviewAllow", False)
        if not isinstance(dependency_review_allow, bool):
            raise RegistryError(
                f"{exception_id}: dependencyReviewAllow must be true or false."
            )
        for advisory in advisories:
            if not ADVISORY_PATTERN.fullmatch(advisory):
                raise RegistryError(f"{exception_id}: malformed advisory id {advisory!r}.")
            normalized = advisory.upper()
            if normalized in seen_advisory_ids:
                raise RegistryError(f"Advisory {advisory} appears in more than one exception.")
            seen_advisory_ids.add(normalized)
            if advisory.upper().startswith("PYSEC-"):
                expected_pip_ignores.add(advisory.upper())
            if dependency_review_allow and advisory.upper().startswith("GHSA-"):
                expected_dependency_review_allows.add(advisory.upper())
        if dependency_review_allow and not any(
            advisory.upper().startswith("GHSA-") for advisory in advisories
        ):
            raise RegistryError(
                f"{exception_id}: dependencyReviewAllow requires a GHSA id."
            )

        _require_text_list(raw_entry, "compensatingControls", exception_id)
        evidence = _require_text_list(raw_entry, "validationEvidence", exception_id)
        references = _require_text_list(raw_entry, "upstreamReferences", exception_id)
        if any(not reference.startswith("https://") for reference in references):
            raise RegistryError(f"{exception_id}: upstream references must use HTTPS.")
        renewal_history = raw_entry.get("renewalHistory")
        if not isinstance(renewal_history, list):
            raise RegistryError(f"{exception_id}: renewalHistory must be a list.")

        cadence = raw_entry.get("reviewCadenceDays", default_cadence)
        if not isinstance(cadence, int) or cadence < 1:
            raise RegistryError(f"{exception_id}: reviewCadenceDays must be positive.")
        created = _parse_date(raw_entry["createdDate"], "createdDate", exception_id)
        reviewed = _parse_date(raw_entry["lastReviewedDate"], "lastReviewedDate", exception_id)
        expires = _parse_date(raw_entry["expiresDate"], "expiresDate", exception_id)
        if reviewed < created:
            raise RegistryError(f"{exception_id}: last review predates creation.")
        if reviewed > today:
            raise RegistryError(f"{exception_id}: last review is in the future.")
        if expires < today:
            raise RegistryError(f"{exception_id}: exception expired on {expires.isoformat()}.")
        if (today - reviewed).days > cadence:
            raise RegistryError(
                f"{exception_id}: review is older than its {cadence}-day cadence."
            )

        relative_lock = Path(raw_entry["lockPath"])
        if relative_lock.is_absolute() or ".." in relative_lock.parts:
            raise RegistryError(f"{exception_id}: lockPath must stay inside the repository.")
        lock_path = repo_root / relative_lock
        if not lock_path.is_file():
            raise RegistryError(f"{exception_id}: lock file is missing: {relative_lock}.")
        package = raw_entry["package"]
        version = raw_entry["pinnedVersion"]
        exact_pin = re.compile(
            rf"^{re.escape(package)}=={re.escape(version)}$", re.IGNORECASE | re.MULTILINE
        )
        if not exact_pin.search(lock_path.read_text(encoding="utf-8")):
            raise RegistryError(
                f"{exception_id}: {package}=={version} is not pinned in {relative_lock}."
            )

        for evidence_path in evidence:
            candidate = Path(evidence_path)
            if candidate.is_absolute() or ".." in candidate.parts:
                raise RegistryError(f"{exception_id}: validation evidence leaves the repository.")
            if not (repo_root / candidate).exists():
                raise RegistryError(
                    f"{exception_id}: validation evidence is missing: {evidence_path}."
                )

        if exception_id not in security_text:
            raise RegistryError(f"{exception_id}: SECURITY.md does not reference this exception.")
        if enforcement == "pip-audit-ignore" and not any(
            advisory.upper().startswith("PYSEC-") for advisory in advisories
        ):
            raise RegistryError(f"{exception_id}: pip-audit-ignore requires a PYSEC id.")
        if enforcement == "dependabot-open" and any(
            advisory.upper().startswith("PYSEC-") for advisory in advisories
        ):
            raise RegistryError(
                f"{exception_id}: dependabot-open exceptions must not silently add pip ignores."
            )

    workflow_ignores = {
        match.upper()
        for match in re.findall(r"--ignore-vuln\s+(PYSEC-\d{4}-\d+)", workflow_text)
    }
    if workflow_ignores != expected_pip_ignores:
        missing = sorted(expected_pip_ignores - workflow_ignores)
        untracked = sorted(workflow_ignores - expected_pip_ignores)
        raise RegistryError(
            "pip-audit ignore drift detected. "
            f"Missing from workflow: {missing or 'none'}; "
            f"untracked in workflow: {untracked or 'none'}."
        )

    dependency_review_values = re.findall(
        r"^\s*allow-ghsas:\s*([^#\n]+)", workflow_text, re.MULTILINE
    )
    workflow_dependency_review_allows = {
        value.upper()
        for raw_value in dependency_review_values
        for value in re.split(r"[\s,]+", raw_value.strip().strip("'\""))
        if value
    }
    if workflow_dependency_review_allows != expected_dependency_review_allows:
        missing = sorted(
            expected_dependency_review_allows - workflow_dependency_review_allows
        )
        untracked = sorted(
            workflow_dependency_review_allows - expected_dependency_review_allows
        )
        raise RegistryError(
            "dependency-review allowlist drift detected. "
            f"Missing from workflow: {missing or 'none'}; "
            f"untracked in workflow: {untracked or 'none'}."
        )


def main() -> int:
    repo_root = Path(__file__).resolve().parents[1]
    registry_path = repo_root / "tools" / "security-exceptions.json"
    workflow_path = repo_root / ".github" / "workflows" / "build.yml"
    security_path = repo_root / "SECURITY.md"
    try:
        registry = json.loads(registry_path.read_text(encoding="utf-8"))
        validate_registry(
            registry,
            repo_root,
            workflow_path.read_text(encoding="utf-8"),
            security_path.read_text(encoding="utf-8"),
        )
    except (OSError, json.JSONDecodeError, RegistryError) as exc:
        print(f"Security exception validation failed: {exc}", file=sys.stderr)
        return 1
    print(
        f"Security exception validation passed: {len(registry['exceptions'])} "
        "temporary exceptions are current and fully tracked."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

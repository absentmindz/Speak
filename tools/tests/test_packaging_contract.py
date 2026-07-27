from __future__ import annotations

import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


class PackagingUpgradeContractTests(unittest.TestCase):
    def test_installer_preserves_machine_wide_upgrade_identity(self) -> None:
        installer = (REPOSITORY_ROOT / "packaging" / "Speak.iss").read_text(
            encoding="utf-8"
        )

        self.assertIn("AppId={{D8A12B4C-1234-5678-ABCD-123456789ABC}", installer)
        self.assertIn("DefaultDirName={autopf}\\{#MyAppName}", installer)
        self.assertIn("PrivilegesRequired=admin", installer)
        self.assertNotIn("PrivilegesRequired=lowest", installer)
        self.assertIn('Excludes: "appsettings.json"', installer)
        self.assertIn(
            'Source: "stage\\App\\appsettings.json"; DestDir: "{app}"; '
            "Flags: onlyifdoesntexist",
            installer,
        )
        self.assertIn("HasKnownModelLayout(CurrentUserModelsRoot)", installer)
        self.assertIn("HasKnownModelLayout(LocalMachineModelsRoot)", installer)
        self.assertLess(
            installer.index("HasKnownModelLayout(CurrentUserModelsRoot)"),
            installer.index("HasKnownModelLayout(LocalMachineModelsRoot)"),
        )
        self.assertIn(
            "RegWriteStringValue(HKLM64, 'SOFTWARE\\Speak', 'ModelsRoot'",
            installer,
        )
        self.assertIn("LegacyUninstallKey", installer)
        self.assertIn("RegDeleteKeyIncludingSubkeys(HKLM64, LegacyUninstallKey)", installer)
        self.assertIn(
            'Type: files; Name: "{commondesktop}\\{#MyAppName}.lnk"',
            installer,
        )
        self.assertIn(
            'Type: dirifempty; Name: "{commonprograms}\\{#MyAppName}"',
            installer,
        )
        self.assertIn("Check: ShouldCreateProgramsShortcut", installer)
        self.assertIn('Type: files; Name: "{app}\\uninstall.bat"', installer)
        self.assertIn("DeleteFile(ExpandConstant('{app}\\uninstall.bat'))", installer)
        self.assertNotIn(
            "DeleteFile(ExpandConstant(\n        "
            "'{commonprograms}\\{#MyAppName}\\{#MyAppName}.lnk'))",
            installer,
        )


if __name__ == "__main__":
    unittest.main()

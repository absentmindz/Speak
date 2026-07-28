# Speak packaging

The canonical release build produces exactly four files in
`packaging/artifacts/`:

- `Speak-<version>-Setup.exe` — the self-contained, machine-wide Windows app
  installer;
- `Speak-<version>-win-x64-portable.zip` — the same self-contained application
  for portable use;
- `Speak-<version>.spdx.json` — the SPDX 2.2 software bill of materials;
- `SHA256SUMS.txt` — one SHA-256 entry for each of the other three assets.

The installer requires administrator approval and preserves Speak 0.5's app
identity, model-root registry fallback, and an existing local
`appsettings.json`, so an upgrade replaces the previous installation without
discarding configured runtime or model paths.

Python, CUDA, FFmpeg, model weights, user settings, history, recordings,
environment variables, and API keys are **not** copied from the build
computer. The app and installer include the .NET runtime, but local Whisper
and TTS still require separately managed Python environments, dependencies,
and model weights. Cloud transcription does not require local Python.

## Build

Prerequisites:

- the .NET 8.0.422 SDK selected by `global.json`;
- Inno Setup (CI pins Chocolatey package `innosetup` 6.7.1).

```powershell
.\packaging\build-packages.ps1
```

Use `-IsccPath` when Inno Setup is installed outside the standard locations.
Use `-NoRestore` only after a successful locked restore. The script installs
Microsoft's SBOM tool at pinned version 4.1.5 into the disposable staging
directory unless `-SbomToolPath` is supplied.

The script is the only packaging path used by CI. It performs a self-contained
`win-x64` publish, validates and smoke-tests the publish tree, generates the
SBOM, builds the ZIP and exactly one installer, then creates checksums only
after every release asset exists. Finally it calls the non-destructive verifier:

```powershell
.\packaging\verify-release-assets.ps1 `
  -ArtifactsRoot .\packaging\artifacts `
  -Version 0.5.2
```

The verifier rejects missing or extra files, duplicate or incomplete checksum
entries, hash mismatches, unsafe ZIP paths, a malformed installer header, and
a non-SPDX-2.2 SBOM. For version tags, a dedicated least-privilege job runs the
verifier again and then creates GitHub-hosted build-provenance attestations for
all four release assets. The release job remains gated on that attestation job
and independently revalidates the checksum file before publishing. Provenance
attestations supplement rather than replace `SHA256SUMS.txt`.

## Offline model pack

An offline model pack is not a release asset and production remains disabled.
Do not attempt to distribute one until the repository contains an audited,
immutable provenance manifest for every model file, including expected hashes
and sizes, reparse-point handling, and rejection of unexpected files.

## Release limitations

NuGet dependencies are restored from lock files and packaging tools are pinned,
but the Inno Setup installer is not currently Authenticode-signed. Windows may
display an unknown-publisher warning. Verify `SHA256SUMS.txt` before running a
downloaded installer or portable build. Do not describe an artifact as signed
or trusted until its checksum, malware scan, license bundle, and—when
available—digital signature have been independently verified.

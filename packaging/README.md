# Speak packaging

The packaging process is intentionally split into independently verifiable
artifacts:

- `Speak-<version>-win-x64-portable.zip` contains the self-contained .NET
  desktop application, the reviewed Python worker source files, the portable
  configuration, and license notices.
- `Speak-<version>-Setup.exe` installs the same application under
  `%ProgramFiles%\Speak` for all Windows users and requires administrator
  approval. It preserves Speak 0.5's scope, path, application identity,
  model-root registry fallback, and existing local `appsettings.json` so an
  upgrade replaces the previous installation without discarding configured
  runtime/model paths.
- The separately installed offline model pack is planned but production is
  currently disabled. It will remain disabled until the repository contains
  an audited, immutable provenance manifest for every model file.

Python, CUDA, FFmpeg, model weights, user settings, history, recordings,
environment variables, and API keys are **not** copied from the build
computer. Local Whisper and TTS therefore require a separately managed Python
environment unless a future signed runtime package is provided.

## Build

Prerequisites:

- the .NET 8 SDK selected by `global.json`;
- Inno Setup 6 or 7 for installer builds.

```powershell
# Portable ZIP + app installer only
.\packaging\build-packages.ps1 -SkipModelPack

# Portable ZIP only
.\packaging\build-packages.ps1 -SkipInstaller
```

Do not attempt a model-pack build yet. The script and Inno definition fail
closed until model provenance, expected hashes and sizes, reparse-point
handling, and extra-file rejection are independently auditable.

The build validates the publish tree, emits a NuGet dependency inventory and
build metadata, and writes SHA-256 hashes for every deliverable to
`packaging/artifacts/SHA256SUMS.txt`. The application ZIP and installer do not
contain model weights.

## Release limitations

Artifacts are reproducible from pinned NuGet lock files, but the Inno Setup
executables are not currently Authenticode-signed. Windows may display an
unknown-publisher warning. Do not describe an artifact as trusted or complete
until its checksum, malware scan, license bundle, and—when available—digital
signature have been independently verified.

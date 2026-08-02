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
discarding configured runtime or model paths. If it detects the complete
edition's manifest, it removes only that edition's app-local Whisper runtime,
FFmpeg runtime, known support/license evidence files, and manifest after the
Standard payload copies successfully. Unknown files in the support/license
directories, external models, custom configured paths, and user data remain
untouched.

CUDA, FFmpeg, model weights, user settings, history, recordings, environment
variables, and API keys are **not** copied from the build computer. The app and
installer include the .NET runtime plus an audited, relocation-safe CPython
3.14.6 command runtime for edge-tts 7.2.8. A deliberately configured non-empty
`EdgeTtsExecutablePath` remains a user override; otherwise the packaged runtime
is auto-discovered. Microsoft neural speech still requires internet and sends
selected text to Microsoft's service. Local
Whisper and model-backed TTS engines still require separately managed runtimes
and model weights unless the complete installer is used for the Whisper base
CPU baseline.

## Build

Prerequisites:

- the .NET SDK selected by `global.json`;
- Inno Setup;
- Python 3.14.6 with `pip` for isolated wheel staging;
- a Sigstore CLI; and
- the reviewed source cache described below, including the Python archive and
  Edge TTS wheelhouse. CI and clean local environments can hydrate only these
  hash-pinned inputs with `prepare-edge-tts-source-cache.ps1`; the requirement
  lock and wheel manifest remain tracked in the repository.

```powershell
.\packaging\prepare-edge-tts-source-cache.ps1 `
  -DestinationRoot D:\release-cache\speak-edge

.\packaging\build-packages.ps1 `
  -SourceCacheRoot D:\release-cache\speak-edge `
  -BuildPythonPath C:\Python314\python.exe `
  -SigstorePath C:\Tools\sigstore.exe
```

Use `-IsccPath` when Inno Setup is installed outside the standard locations.
Use `-NoRestore` only after a successful locked restore. The script installs
Microsoft's SBOM tool at pinned version 4.1.5 into the disposable staging
directory unless `-SbomToolPath` is supplied. The portable verifier extracts
the ZIP to a different path and runs `python -B -I -m edge_tts --version`, proving
that no machine-specific console launcher or Python path was embedded.

The script is the only packaging path used by CI. It performs a self-contained
`win-x64` publish, validates and smoke-tests the publish tree, generates the
SBOM, builds the ZIP and exactly one installer, then creates checksums only
after every release asset exists. Finally it calls the non-destructive verifier:

```powershell
.\packaging\verify-release-assets.ps1 `
  -ArtifactsRoot .\packaging\artifacts `
  -Version 0.5.3
```

The verifier rejects missing or extra files, duplicate or incomplete checksum
entries, hash mismatches, unsafe ZIP paths, a malformed installer header, and
a non-SPDX-2.2 SBOM. For version tags, a dedicated least-privilege job runs the
verifier again and then creates GitHub-hosted build-provenance attestations for
all four release assets. The release job remains gated on that attestation job
and independently revalidates the checksum file before publishing. Provenance
attestations supplement rather than replace `SHA256SUMS.txt`.

## Complete offline installer (CPU Whisper baseline)

`build-complete-installer.ps1` is a separate, fail-closed packaging path for a
single **complete** Setup EXE. It combines a self-contained `win-x64` Speak
publish with an app-local Python 3.14.6 runtime, the pinned CPU-only Whisper
wheel set, the audited Edge TTS 7.2.8 wheel set, the audited FFmpeg
executable/DLL payload, the Microsoft VC++ x64 redistributable, and OpenAI
Whisper `base.pt`. Whisper and Edge share one embedded Python runtime so the
complete installer does not duplicate CPython. It deliberately excludes CUDA
Python/Torch payloads, large Whisper checkpoints, model-backed local TTS
runtimes/models, and other optional AI payloads. The Edge command runtime
installs offline, but Microsoft neural voices still require internet and send
selected text to Microsoft's service. The installed defaults remain
`whisper-base`, `auto`, Qwen3 CustomVoice, and Aiden; the bundled CPU-only Torch
runtime makes Whisper `auto` resolve to CPU on the baseline installation.

The builder does not download or trust floating inputs. Populate a dedicated,
reviewed source cache with these exact files from
`packaging/complete/sources.lock.json`:

```text
<cache>\
  python-3.14.6-embed-amd64.zip
  python-3.14.6-embed-amd64.zip.sigstore
  VC_redist.x64.exe
  base.pt
  ffmpeg-n8.1.2-31-g8c9502e9b0-win64-lgpl-shared-8.1.zip
  whisper-cpu-wheelhouse\
    <every pinned .whl listed by the manifest>
  edge-tts-wheelhouse\
    <every pinned .whl listed by the manifest>
```

The audited manifests are tracked as
`packaging/complete/whisper-cpu-wheelhouse.manifest.json` and
`packaging/edge-tts-wheelhouse.manifest.json`. Each manifest binds to its
matching exact-pin lock (`tools/requirements-whisper-cpu.lock.txt` or
`tools/requirements-edge-tts.lock.txt`) and lists every cached wheel's source
URL, filename, version, declared SPDX license, size, and SHA-256. The build rejects
source/cache reparse points, missing or unexpected files, mismatched versions,
byte counts, digests, an invalid Python Sigstore identity, and an invalid VC++
Authenticode signer. The immutable BtbN LGPL shared FFmpeg archive is verified
before extraction; only its lock-listed `ffmpeg.exe` and required DLLs are
staged, and each staged file must match its audited size and SHA-256.

Prerequisites beyond the standard release builder are:

- a Sigstore CLI that can verify Python's bundle identity;
- a reviewed offline source cache as above;
- Inno Setup;
- Python 3.14.6 with `pip` for the isolated `--target` wheel install; and
- the .NET SDK selected by `global.json` unless `-AppPublishRoot` supplies an
  already verified self-contained publish tree.

```powershell
.\packaging\build-complete-installer.ps1 `
  -SourceCacheRoot D:\release-cache\speak-complete `
  -BuildPythonPath C:\Python314\python.exe `
  -SigstorePath C:\Tools\sigstore.exe
```

Use `-PythonArchivePath`, `-PythonSigstoreBundlePath`,
`-WhisperWheelCache`, `-WhisperWheelManifestPath`, `-EdgeTtsWheelCache`,
`-EdgeTtsWheelManifestPath`, `-WhisperBaseModelPath`,
`-FfmpegArchivePath`, `-VcRedistPath`, and `-AppPublishRoot` to pass explicit
reviewed sources. `-SkipRuntimeHealth` is an explicit diagnostic escape hatch;
normal builds load `base.pt` on CPU, import Whisper/Torch, and execute FFmpeg
before and after compiling the installer.

The only artifact in `packaging/artifacts/complete/` is
`Speak-<version>-Complete-Offline-Setup.exe`; `DiskSpanning=no` prevents
external `.bin` slices. The installer keeps Python app-local, removes both installer-owned embedded
runtime layouts before replacement so a standard-to-complete upgrade cannot
retain a duplicate dedicated Edge runtime, writes model-root registry values
only after a successful model copy, preserves valid existing `appsettings.json`
files and every customized path/setting while backing up and replacing only an
exact match for the released incomplete v0.5.2 local-Whisper defaults (whose
wrapper and server-script defaults were already nonempty), preserves an existing
hash-valid `base.pt`,
installs VC++ silently only when the installed runtime is older than
the bundled minimum, and launches Speak after an interactive successful setup.
`verify-complete-installer.ps1` validates the staged component manifest,
hashes, installer PE/header/version, Whisper and Edge runtime imports, Edge
module relocation, CPU model load, FFmpeg health, and the installed SPDX 2.3
complete-offline CPU SBOM. The installer also retains the source lock,
wheelhouse manifests, requirement locks, and
third-party notices under `{app}\Support` and `{app}\Licenses`. The shipped
customer runtime removes pip, generated bytecode, and path-bound console
launchers. For reversible side-by-side validation, pass
`-TestMode` to the builder and install with
`/DIR=<isolated path> /ModelsRoot=<isolated model root>`; the test build uses a
separate AppId, per-user install scope, and registry subkey, and deliberately
skips executing the embedded VC++ redistributable while still validating and
packaging it. Test-mode stage/artifacts are isolated under
`packaging/stage/complete-test/` and `packaging/artifacts/complete-test/`, so a
production complete-installer build remains intact for final local review.

The generated stage and complete-installer EXE live beneath already ignored
`packaging/stage/` and `packaging/artifacts/`; never add the runtime, wheels,
model, or generated installer binary to Git.

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

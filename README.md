<p align="center">
  <img src="speak_logo.png" alt="Speak logo" width="120">
</p>

<h1 align="center">Speak</h1>

<p align="center">
  Privacy-conscious dictation and text-to-speech for Windows.
</p>

<p align="center">
  <a href="https://github.com/absentmindz/Speak/actions/workflows/build.yml">
    <img src="https://github.com/absentmindz/Speak/actions/workflows/build.yml/badge.svg" alt="Build status">
  </a>
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4?logo=windows" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/license-Apache--2.0-green" alt="Apache 2.0">
</p>

> Speak is currently a pre-release source project. Review the security and
> privacy notes before using it with sensitive audio or text.

## What it does

- Global-hotkey dictation into Windows applications.
- Local Whisper transcription when a compatible Python environment and model
  are configured.
- Optional Groq cloud transcription.
- Local Qwen3, Tortoise, and Chatterbox text-to-speech integrations.
- Optional local or remote LLM transcript polishing.
- Local history, dictionary, correction learning, and voice-profile tools.
- A token-protected loopback REST integration that is disabled by default.

Local engines process audio on the computer. Cloud STT and remote LLM
polishing send audio or text to the configured provider. They are not the same
privacy mode. See [PRIVACY.md](PRIVACY.md) for the exact boundaries.

## Screenshots

<p align="center">
  <img src="docs/screenshots/main.png" alt="Speak dictation screen" width="780">
</p>

<p align="center">
  <img src="docs/screenshots/history.png" alt="Speak history screen" width="780">
</p>

<p align="center">
  <img src="docs/screenshots/voice-profile.png" alt="Speak voice profile" width="780">
</p>

<p align="center">
  <img src="docs/screenshots/dictionary.png" alt="Speak dictionary" width="780">
</p>

<p align="center">
  <img src="docs/screenshots/audio-studio.png" alt="Speak audio studio" width="780">
</p>

The repository screenshots contain demonstration data only. Do not attach
screenshots containing real transcripts, usernames, paths, notifications, or
taskbar details to an issue.

## Requirements

- Windows 10 version 1809 or newer, or Windows 11, on x64 hardware.
- The .NET 8 SDK to build from source. Published artifacts are self-contained.
- For local Whisper/TTS: compatible Python environments, model weights, and
  any required CUDA/FFmpeg dependencies.
- For cloud transcription: a provider API key stored in an environment
  variable, never in the repository.

GPU acceleration is optional for the application but strongly recommended for
the larger local models.

## Build from source

```powershell
git clone https://github.com/absentmindz/Speak.git
cd Speak

dotnet restore Speak.sln --locked-mode
dotnet build Speak.sln -c Release --no-restore
dotnet test Speak.sln -c Release --no-build
dotnet run --project Speak.csproj
```

A clean checkout starts with safe portable defaults; `appsettings.json` is
optional. To configure local runtimes, copy the template:

```powershell
Copy-Item appsettings.template.json appsettings.json
```

The local `appsettings.json` is ignored by Git, copied to development output,
and explicitly excluded from publish artifacts.

## Configuration

Portable tokens can be used instead of machine-specific paths:

| Token | Resolves to |
|---|---|
| `{AppDir}` | the directory containing `Speak.exe` |
| `{LocalAppData}` | the current user's local application-data directory |
| `{CommonAppData}` | the shared Windows application-data directory |
| `{UserProfile}` | the current user's profile directory |
| `{ModelsRoot}` | the validated model root selected by configuration, environment, or installer |

Set `SPEAK_MODELS_ROOT` to override model discovery. Optional
`SPEAK_FFMPEG_BIN` and `SPEAK_SOX_BIN` variables can point to custom binary
directories without embedding machine-specific paths in source or JSON.
Set `SPEAK_DATA_ROOT` to use an isolated data directory. An explicit data-root
override never imports history, recordings, logs, or settings from the normal
local application-data directory; the one-time legacy import runs only for
the default data location. Speak records completion of that import so clearing
history or recordings cannot cause old legacy copies to reappear on restart.
Local worker port conflicts can be resolved with
`SPEAK_WHISPER_SERVER_PORT`, `SPEAK_QWEN_CUSTOMVOICE_WORKER_PORT`, and
`SPEAK_QWEN_BASE_WORKER_PORT`. Their hosts remain fixed to loopback for
security. Cloud API keys are read from the environment variable named by the
configuration (`GROQ_API_KEY` by default); the key value does not belong in
JSON.

Correction auto-learning is off by default because it may inspect text in the
foreground application. Enable it only after reviewing the setting's privacy
impact.

### Local REST integration

The loopback REST API is disabled unless both of these environment variables
are configured:

```text
SPEAK_ENABLE_REST_API=1
SPEAK_REST_API_TOKEN=<a long random secret>
SPEAK_REST_API_PORT=19876
```

`SPEAK_REST_API_PORT` is optional; the default is `19876`.

Treat the token like a password. Do not expose the loopback port through a
proxy, firewall rule, tunnel, or port-forward.

## Packages and releases

CI publishes a self-contained Windows x64 portable ZIP for successful builds.
Tagged builds create a GitHub release containing that ZIP, an SPDX 2.2 software
bill of materials, and SHA-256 checksums for both. The SBOM is also embedded in
the portable ZIP. A release is not an offline-AI bundle: model weights and
Python/CUDA runtimes are separate.

Maintainers can build the per-user Inno Setup installer and optional
disk-spanned model pack with
[`packaging/build-packages.ps1`](packaging/build-packages.ps1). The packaging
process never copies the maintainer's virtual environment, settings, history,
recordings, or API keys. See [packaging/README.md](packaging/README.md).

Installers are not currently Authenticode-signed. Verify
`SHA256SUMS.txt` and expect Windows to show an unknown-publisher warning.

## Repository layout

```text
MaxFlowWindows/          WPF application windows and interaction logic
MaxFlowWindows.Core/     configuration, persistence, STT/TTS, and integrations
*.xaml                   editable WPF window layouts
tools/                   reviewed Python worker source
tests/Speak.Tests/       automated .NET tests
packaging/               publish validation and Inno Setup packaging
.github/                 CI, security scanning, and contribution templates
```

## Contributing and security

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. Use
[GitHub's private vulnerability reporting](https://github.com/absentmindz/Speak/security/advisories/new)
for security issues; do not post secrets, private audio, or transcripts in a
public issue. The full policy is in [SECURITY.md](SECURITY.md).

## License

Speak is licensed under the [Apache License 2.0](LICENSE). Attribution
information is in [NOTICE](NOTICE).

## Acknowledgements

- [OpenAI Whisper](https://github.com/openai/whisper)
- [Qwen3-TTS](https://huggingface.co/Qwen)
- [NAudio](https://github.com/naudio/NAudio)
- [Silero VAD](https://github.com/snakers4/silero-vad)

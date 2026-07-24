# Contributing to Speak

Thank you for your interest in contributing to Speak! Whether it's a bug report, feature idea, code improvement, or documentation fix — all contributions are welcome. 🎙️

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [Ways to Contribute](#ways-to-contribute)
- [Reporting Bugs](#reporting-bugs)
- [Suggesting Features](#suggesting-features)
- [Development Setup](#development-setup)
- [Pull Request Process](#pull-request-process)
- [Coding Style](#coding-style)

---

## Code of Conduct

Please be respectful and constructive in all interactions. We follow a simple rule: **treat others as you'd like to be treated**.

---

## Ways to Contribute

| Type | How |
|---|---|
| 🐛 Bug report | [Open an Issue](../../issues/new?template=bug_report.md) |
| 💡 Feature request | [Open an Issue](../../issues/new?template=feature_request.md) |
| 🔧 Code fix / feature | Fork → branch → PR |
| 📖 Docs improvement | Edit markdown files and open a PR |
| 🌍 Translation | Open an issue to discuss |

---

## Reporting Bugs

Before filing a bug, please check [existing issues](../../issues) to avoid duplicates.

When opening a bug report, include:

- **OS version** (Windows 10/11, build number)
- **Speak version** (visible in the title bar or About dialog)
- **Transcription/TTS engine** you were using
- **Steps to reproduce** the problem
- **What you expected** to happen vs. what actually happened
- **Crash report** if one was written to `%LocalAppData%\Speak\crashes\`
- **Log file** from `%LocalAppData%\Speak\logs\` (remove any sensitive content first)

---

## Suggesting Features

Feature requests are very welcome! Please describe:

- **The problem** you're trying to solve
- **The solution** you have in mind
- **Alternatives** you've considered
- Any relevant examples from other tools

---

## Development Setup

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Windows 10/11 x64
- Visual Studio 2022 or VS Code with the C# extension
- *(Optional)* NVIDIA GPU + CUDA for local Whisper/TTS testing

### Getting Started

```powershell
# 1. Fork and clone
git clone https://github.com/YOUR_USERNAME/Speak.git
cd Speak

# 2. Set up your settings
Copy-Item appsettings.template.json appsettings.json
# Edit appsettings.json with your local paths

# 3. Build
dotnet build Speak.csproj

# 4. Run
dotnet run --project Speak.csproj
```

### Project Structure

```
Speak/
├── MaxFlowWindows/          # WPF UI — windows, views, app entry point
├── MaxFlowWindows.Core/     # Core logic — STT, TTS, config, API server
├── speak/                   # VAD model (silero_vad.onnx)
├── tools/                   # Python worker scripts (Whisper, Qwen TTS)
├── packaging/               # InnoSetup installer scripts
└── .github/workflows/       # CI/CD
```

---

## Pull Request Process

1. **Fork** the repository and create your branch from `main`:
   ```
   git checkout -b fix/my-bug-fix
   ```

2. **Make your changes** — keep commits focused and descriptive.

3. **Test** your changes locally (build + run).

4. **Keep your branch up to date**:
   ```
   git fetch origin
   git rebase origin/main
   ```

5. **Open a Pull Request** against `main`. Fill in the PR template with:
   - What the change does
   - How to test it
   - Any screenshots (for UI changes)

6. A maintainer will review your PR. Please respond to feedback promptly.

> **Note**: By submitting a PR, you agree your contribution is licensed under the same [Apache 2.0 license](LICENSE) as the project.

---

## Coding Style

- **C#**: Follow standard C# conventions. Use `var` where the type is obvious. Prefer expression-bodied members for simple methods.
- **Naming**: PascalCase for public members, camelCase for locals, `_camelCase` for private fields.
- **Comments**: Write comments for *why*, not *what*. Keep them current.
- **No secrets**: Never commit API keys, passwords, or personal file paths. Use `appsettings.json` (which is gitignored) for local config.
- **Backup files**: Don't commit `.bak` or `.bak-*` files — they're gitignored.

---

## Questions?

Open a [Discussion](../../discussions) or [Issue](../../issues) — we're happy to help!

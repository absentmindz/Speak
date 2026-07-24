# Speak packaging

This directory builds two distributable packages:

- `Speak-0.5.0-Complete-Setup.exe`: Speak, self-contained .NET, a shared portable Python 3.11 runtime, Torch/CUDA libraries, Whisper, Qwen TTS, FFmpeg, and the Microsoft Visual C++ runtime.
- `Speak-0.5.0-Offline-Models.zip`: optional offline weights for Whisper large-v3, Qwen3 CustomVoice 1.7B, and Qwen3 Base 1.7B. Extract the ZIP before running its setup program; all `.bin` slices must remain beside the setup EXE.

The application installer contains no user settings, history, recordings, environment variables, or API-key values. `GROQ_API_KEY` is only the name of the environment variable that a user may configure on the destination computer.

At installation time, the user chooses a models folder. Speak stores that folder in `HKLM\SOFTWARE\Speak\ModelsRoot`. At startup, model lookup follows this order:

1. `SPEAK_MODELS_ROOT`
2. a configured folder with a validated Speak model layout
3. the installer registry value when it contains a validated model layout
4. validated common model folders on fixed drives
5. the installer-selected folder, even when it is waiting for the optional model pack
6. `%ProgramData%\Speak\Models`

The offline model pack writes the same registry value, so Speak detects its models after restart without editing personal settings.

Final deliverables and their SHA-256 hashes are recorded in `artifacts\SHA256SUMS.txt`. The installer is not code-signed, so Windows may show an unknown-publisher warning. The destination computer still needs a compatible NVIDIA display driver for CUDA acceleration; the application runtimes and CUDA user-mode libraries are included.

# Contributing to Speak

Thank you for helping improve Speak.

By participating, you agree to follow the
[Code of Conduct](CODE_OF_CONDUCT.md). Contributions are licensed under the
same [Apache License 2.0](LICENSE) as the project.

## Before opening an issue

- Search [existing issues](https://github.com/absentmindz/Speak/issues).
- Use the bug or feature issue form.
- Remove transcripts, recordings, API keys, usernames, local paths,
  notifications, and taskbar details from logs and screenshots.
- Report vulnerabilities privately according to [SECURITY.md](SECURITY.md).

## Development setup

Requirements are Windows 10/11 x64 and the .NET 8 SDK selected by
`global.json`.

```powershell
git clone https://github.com/YOUR_USERNAME/Speak.git
cd Speak

dotnet restore Speak.sln --locked-mode
dotnet build Speak.sln -c Release --no-restore
dotnet test Speak.sln -c Release --no-build
.\packaging\verify-repository.ps1
```

`appsettings.json` is optional. If local model integration is needed:

```powershell
Copy-Item appsettings.template.json appsettings.json
```

Use portable tokens or values for your own machine. Never commit that file,
an `.env` file, a virtual environment, model weights, audio, logs, or secrets.

## Pull requests

1. Create a focused branch from `main`.
2. Keep unrelated formatting and generated-file changes out of the PR.
3. Add or update tests for behavior changes.
4. Run the build, tests, repository scan, and a clean publish locally.
5. Explain user-visible, privacy, security, and packaging effects in the pull
   request template.
6. Wait for CI and address review feedback.

For a clean publish check:

```powershell
dotnet publish Speak.csproj -c Release --no-restore `
  -o publish-output `
  -p:DebugType=None `
  -p:DebugSymbols=false

.\packaging\verify-publish.ps1 -PublishRoot .\publish-output
```

The publish check launches `Speak.exe` for five seconds. Use `-SkipLaunch`
only in an environment that cannot start a Windows desktop process.

## Style

- Follow standard C# naming and formatting conventions.
- Prefer cancellation-aware asynchronous code for I/O and process work.
- Keep privacy-sensitive features opt-in and fail closed.
- Do not log audio, transcript bodies, credentials, or authorization headers.
- Explain why a non-obvious decision exists instead of narrating the code.
- Update user-facing privacy copy whenever data can leave the computer.

## Dependency and package changes

- Pin direct dependencies and commit updated `packages.lock.json` files.
- Run `dotnet list Speak.sln package --include-transitive --vulnerable`.
- Do not package a developer virtual environment or arbitrary cache directory.
- Add third-party notices for redistributed software or model assets.
- Do not claim an artifact is signed, complete, or reproducible unless the
  release process verifies that claim.

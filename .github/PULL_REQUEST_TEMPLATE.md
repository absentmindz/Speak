## Summary

<!-- What changes, and why? -->

## Verification

- [ ] `dotnet build Speak.sln -c Release`
- [ ] `dotnet test Speak.sln -c Release --no-build`
- [ ] `.\packaging\verify-repository.ps1`
- [ ] Clean publish verification, when packaging/runtime behavior changed

## Risk review

- [ ] I added or updated tests for behavior changes.
- [ ] I did not include secrets, personal paths, private audio/text, logs, model
      weights, build output, or virtual environments.
- [ ] I documented any change to data access, retention, network requests,
      cloud processing, authentication, or permissions.
- [ ] I updated dependency locks and notices when dependencies or redistributed
      assets changed.

## Screenshots

<!-- UI changes only. Use synthetic data and crop OS/user details. -->

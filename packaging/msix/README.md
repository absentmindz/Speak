# Microsoft Store MSIX readiness

This folder is a safe packaging foundation, not a fake Store identity.

## Required Partner Center values

Reserve the product name, then copy these values exactly from Partner Center:

- Package identity name
- Publisher ID (for example `CN=...`)
- Publisher display name
- Store package version

Identity values are case-sensitive. Do not guess them or commit private Partner Center exports.

## Validate the template

```powershell
./packaging/msix/build-msix.ps1 `
  -PackageIdentityName "Speak.Test" `
  -Publisher "CN=Speak Test" `
  -PublisherDisplayName "Speak contributors" `
  -Version "1.5.2.0" `
  -ValidateOnly
```

The public app version remains 0.5.2. The Store package uses a separate monotonically increasing four-part version whose first component is non-zero and whose fourth component is `0`.

## Build a Store candidate

First create the canonical self-contained publish through `packaging/build-packages.ps1`, then run:

```powershell
./packaging/msix/build-msix.ps1 `
  -PackageIdentityName "<Partner Center identity>" `
  -Publisher "<Partner Center publisher ID>" `
  -PublisherDisplayName "<Store publisher name>" `
  -Version "<Store version, e.g. 1.5.2.0>" `
  -PublishRoot ./packaging/stage/App `
  -OutputPath ./packaging/artifacts/Speak-Store.msix
```

The script locates MakeAppx from the Windows SDK, stages the self-contained app, generates required image assets from `speak_logo.png`, renders the manifest, and builds an unsigned MSIX. Microsoft re-signs MSIX/AppX packages submitted through the Store. Sideloaded packages still require your own signing certificate.

## Before submission

1. Inspect the rendered manifest and package contents.
2. Run Windows App Certification Kit.
3. Test clean install, update, launch, microphone access, global shortcut, local data paths, and uninstall on Windows 10 and 11.
4. Confirm the Store identity does not collide with the Inno install during upgrade testing.
5. Upload only after privacy, support, listing, pricing, screenshots, and certification notes are final.

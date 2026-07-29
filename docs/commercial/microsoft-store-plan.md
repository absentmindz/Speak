# Microsoft Store packaging plan

## Recommendation

Publish an **MSIX** rather than submitting the current unsigned Inno Setup EXE. Microsoft documents that Store-submitted MSIX/AppX packages are re-signed by the Store, while submitted EXE/MSI installers must already be Authenticode-signed. The existing Inno installer remains the direct-download channel.

## Repository readiness added now

- `packaging/msix/AppxManifest.xml.template` — identity-safe manifest template.
- `packaging/msix/build-msix.ps1` — validates Partner Center fields and can build an unsigned Store-candidate MSIX with MakeAppx.
- `packaging/msix/README.md` — exact operator workflow and blockers.
- Existing screenshots provide the required listing base; four or more are already available.
- The one-minute demo can be submitted as an optional Store trailer after final review.

## External gates that cannot be completed from source control

1. Open or confirm the correct Partner Center developer account.
2. Decide whether publishing is under an individual identity or a registered company. Microsoft does not support converting an Individual account into a Company account later.
3. Reserve the product name.
4. Copy the exact Package/Identity name, Publisher ID, and Publisher display name from Partner Center.
5. Finalize privacy/support/website URLs and seller contact details.
6. Build with those exact case-sensitive identity values.
7. Run Windows App Certification Kit on the final package and test clean install/update/uninstall on representative Windows 10 and 11 machines.
8. Complete pricing, properties, age rating, package, listing, and certification notes in Partner Center.

## Store listing draft

**Name:** Speak for Windows (subject to Partner Center reservation)

**Short description:** Private, local-first voice writing. Dictate naturally and paste polished text into any Windows application.

**Category:** Productivity

**Core features:**

- Global-hotkey dictation.
- Local Whisper integration when separately configured.
- Optional cloud transcription.
- Personal dictionary and correction learning.
- Local transcript history and voice profile.
- Optional local text-to-speech tools.

**Privacy wording:** Local engines keep submitted audio on the computer. Configured cloud providers receive audio or text only when the corresponding cloud feature is used. No advertising telemetry is configured.

## Version rule

Store package versions use four numeric parts, the first part must be non-zero, and the fourth part must remain `0` for Windows 10/11 submission. Keep this sequence separate from the public semantic version: for example, Speak `0.5.2` can use Store package version `1.5.2.0`, then advance monotonically without changing the public product name.

Official references:

- https://learn.microsoft.com/windows/apps/publish/partner-center/open-a-developer-account
- https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/app-package-requirements
- https://learn.microsoft.com/windows/msix/package/create-app-package-with-makeappx-tool
- https://learn.microsoft.com/windows/apps/publish/publish-your-app/msix/create-app-submission

# Security policy

## Supported versions

Speak is a pre-release project. Security fixes are applied to the latest code
on `main`; older snapshots and unofficial binaries are not supported.

## Report a vulnerability

Use
[GitHub private vulnerability reporting](https://github.com/absentmindz/Speak/security/advisories/new).
Do not open a public issue for a suspected vulnerability.

Include:

- the affected commit or version;
- impact and prerequisites;
- minimal reproduction steps or a proof of concept;
- suggested mitigations, if known.

Do not include real API keys, private recordings, transcripts, or unrelated
personal data. Use synthetic test data.

You should receive an acknowledgement within seven days. Please allow time for
triage and a coordinated fix before public disclosure.

## Security expectations

- Store API keys in environment variables or an operating-system secret store.
- Keep the local REST API disabled unless needed; use a long random token.
- Verify release checksums. Installers are not currently code-signed.
- Obtain model weights and Python packages from their official publishers and
  verify their licenses and hashes.
- Qwen TTS currently pins `transformers==4.57.3` upstream. Speak compensates
  by forcing Qwen model loading offline and rejecting executable, pickle-based,
  symlinked, or custom-code model trees. Do not bypass those checks or use
  untrusted model files; upgrade when Qwen publishes a compatible safe pin.
- Treat cloud STT and remote LLM endpoints as external data processors.

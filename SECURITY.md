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

## Temporary dependency exceptions

Speak does not silently hide release-blocking dependency risk. Temporary exceptions live in
[`tools/security-exceptions.json`](tools/security-exceptions.json), are checked by CI, expire
automatically, and must be reviewed every seven days. CI fails if an exception is malformed,
stale, expired, no longer matches its lock file, or drifts from the exact `pip-audit` and dependency-review allowlists.

Current Qwen-only exceptions, last reviewed **November 14, 2026** and expiring
**December 31, 2026**. The existing approvals and compensating controls were renewed on
November 14, 2026:

| Exception | Dependency | Advisory scope | Why it remains temporary |
|---|---|---|---|
| `SEC-2026-001` | `transformers==4.57.3` | Trainer checkpoint loading | Qwen does not use Trainer; upstream still pins this version. |
| `SEC-2026-002` | `transformers==4.57.3` | Remote attention-kernel configuration | Speak is offline/local-only and rejects the unsafe configuration fields. |
| `SEC-2026-003` | `transformers==4.57.3` | LightGlue nested model loading | LightGlue is not used and remote/custom model code is rejected. |
| `SEC-2026-004` | `transformers==4.57.3` | X-CLIP checkpoint conversion | X-CLIP is not used and pickle/executable model material is rejected. |
| `SEC-2026-005` | `torch==2.12.1+cu126` | `torch.jit.script` memory corruption | Qwen does not use that API; an official matching TorchAudio 2.13 CUDA 12.6 build is not available. The Dependabot alert remains open. |
| `SEC-2026-006` | `setuptools==81.0.0` | macOS source-distribution Unicode exclusion bypass | Speak is Windows-only and consumes wheels; Torch 2.12 requires setuptools below the patched 83.0.0 release. |
| `SEC-2026-007` | `accelerate==1.12.0` | Sharded checkpoint `weight_map` path traversal and denial of service | qwen-tts pins this release and no patched upstream version is available. |
| `SEC-2026-008` | `transformers==4.57.3` | Chat-template path traversal and pre-consent custom-code download | qwen-tts pins 4.57.3; the patched 5.10 line is not Qwen-compatible and one advisory has no fix. |
| `SEC-2026-009` | `setuptools==81.0.0` | Windows legacy-wheel namespace-package path escape | Torch 2.12.1 requires setuptools<82; Speak installs pinned prebuilt wheels only. |
| `SEC-2026-010` | `pillow==12.3.0` | Multiple image-decoding and parsing advisories | Gradio 6.17.3 requires pillow<13; newer Gradio needs huggingface-hub>=1.16, but Transformers 4.57.3 requires <1.0. |

The separate Whisper runtime is pinned to patched `torch==2.13.0+cu126` in 0.5.2.
Removing an exception requires a tested compatible upstream stack, removal of the corresponding
audit ignore or open alert, and the full worker/security regression suite.

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

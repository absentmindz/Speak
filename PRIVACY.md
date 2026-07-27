# Privacy

Speak handles microphone audio and dictated text. The selected engine
determines whether that data stays on the computer.

## Local processing

Local Whisper and local text-to-speech run through user-configured Python
workers on the same computer. Speak may store settings, history, correction
data, recordings, generated audio, logs, and crash reports under its local
application-data directory. Retention and history controls are available in
the application.

The recording-retention selector applies to WAV files in Speak's active
`recordings` directory, including its subdirectories. `Keep forever` disables
automatic recording deletion; a positive value permanently deletes recordings
older than that many days and takes effect as soon as the setting is saved.
Generated text-to-speech and voice-clone outputs are preserved until the user
deletes them. Application log files older than 90 days are removed
automatically.

When `SPEAK_DATA_ROOT` is explicitly configured, Speak treats that directory
as an isolation boundary and does not copy settings, history, recordings, or
logs from its normal local application-data directory. The automatic legacy
import applies only when the default data location is used. On a Speak 0.5
upgrade, it first imports the same model-adjacent or fixed `OpenClawData`
location that the earlier executable selected, including settings, history,
recordings, and logs. Speak stores a versioned completion marker outside the
clearable data-file families, so clearing history or recordings does not
re-import old legacy copies on restart.

Clearing history requires confirmation. It erases the history database and
its recovery copies, and deletes only the exact linked audio files located
inside Speak's `recordings` or legacy `recordings-archive` directories. It does
not delete external audio files or unrelated files with the same name.

Correction auto-learning is disabled by default. When enabled, it may inspect
text in the foreground application after a paste so it can compare a
correction. Do not enable it in password managers, terminals containing
secrets, healthcare systems, or other sensitive applications.

## Cloud processing

Cloud STT uploads recorded audio to the configured transcription provider.
Remote LLM polishing sends transcript text and its draft output to the
configured LLM endpoint. Those providers receive and process the submitted
data under their own terms and privacy policies.

Cloud features require deliberate configuration. API key values are read from
environment variables and should never be placed in source control,
screenshots, logs, or support bundles.

## Local REST API

The REST integration is disabled by default. When enabled, it listens only on
the loopback interface and requires a strong bearer token. Other software
running under the same Windows account may still be able to reach loopback
services, so enable it only when needed and never forward its port.

## Network and telemetry

This repository does not configure an analytics or advertising telemetry
service. Cloud features and dependency/model setup may make network requests
when the user selects or invokes them.

## Sharing diagnostics

Logs, crash reports, history exports, recordings, and screenshots can contain
private content or local paths. Review and redact them before sharing. Security
issues should be reported through the private channel in
[SECURITY.md](SECURITY.md).

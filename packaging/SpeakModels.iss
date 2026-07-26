; Offline model-pack production is intentionally disabled.
;
; Speak does not have a checked-in, reviewed provenance manifest containing an
; immutable upstream revision and expected SHA-256/size for every model file.
; A checksum inventory generated from arbitrary local files is not proof of
; provenance. Do not remove this guard until a manifest validating against
; model-pack.manifest.schema.json is checked in and the build script rejects
; reparse points, missing files, hash/size mismatches, duplicate paths, and
; every unapproved extra file before invoking Inno Setup.

#error Speak offline model-pack production is disabled pending audited model provenance.

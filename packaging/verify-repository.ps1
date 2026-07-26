param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath (Join-Path $root ".git") -PathType Container)) {
    throw "Not a Git working tree: $root"
}

$textExtensions = @(
    ".cs", ".csproj", ".props", ".targets", ".sln", ".json", ".config",
    ".xml", ".txt", ".md", ".ps1", ".py", ".cmd", ".bat", ".iss",
    ".yml", ".yaml", ".vbs", ".gitignore"
)
$unixProfilePattern = '(?i)(/' + 'Users/[^/\s"]+|/' + 'home/[^/\s"]+)'
$patterns = [ordered]@{
    "personal Windows path" = '(?i)[A-Z]:\\Users\\[^\\\s"]+'
    "personal Unix path" = $unixProfilePattern
    "developer drive path" = '(?i)\b[D-F]:\\(?:Speak|Models|OpenClaw|workspace)(?:\\|$)'
    "hardcoded tool install path" = '(?i)\b[A-Z]:\\(?:ffmpeg\\bin|Program Files(?: \(x86\))?\\sox-[^\\\s"]+)'
    "OpenAI-style secret" = '\bsk-[A-Za-z0-9_-]{20,}\b'
    "Groq secret" = '\bgsk_[A-Za-z0-9_-]{20,}\b'
    "Hugging Face token" = '\bhf_[A-Za-z0-9]{20,}\b'
    "GitHub token" = '\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})\b'
    "Google API key" = '\bAIza[0-9A-Za-z_-]{30,}\b'
    "AWS access key" = '\bAKIA[0-9A-Z]{16}\b'
    "Slack token" = '\bxox[baprs]-[A-Za-z0-9-]{20,}\b'
    "consumer email address" = '(?i)\b[A-Z0-9._%+-]+@(?:gmail|hotmail|outlook|yahoo)\.[A-Z]{2,}\b'
    "private key block" = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
}

$trackedFiles = & git -C $root ls-files --cached --others --exclude-standard
if ($LASTEXITCODE -ne 0) {
    throw "git ls-files failed."
}

$findings = [System.Collections.Generic.List[string]]::new()
foreach ($relative in $trackedFiles) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        [System.IO.Path]::GetExtension($path) -notin $textExtensions) {
        continue
    }

    $lines = @(Get-Content -LiteralPath $path)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        foreach ($rule in $patterns.GetEnumerator()) {
            if ($lines[$index] -match $rule.Value) {
                $lineWithoutApprovedAttribution = $lines[$index] -replace '(?i)\bjbevain@gmail\.com\b', ''
                $isApprovedAttribution =
                    $rule.Key -eq "consumer email address" -and
                    $relative -eq "THIRD-PARTY-NOTICES.txt" -and
                    $lines[$index] -match '(?i)\bjbevain@gmail\.com\b' -and
                    $lineWithoutApprovedAttribution -notmatch $rule.Value
                if ($isApprovedAttribution) {
                    continue
                }
                $findings.Add("${relative}:$($index + 1) ($($rule.Key))")
            }
        }
    }
}

if ($findings.Count -gt 0) {
    throw "Repository privacy/secret scan failed:`n$($findings -join [Environment]::NewLine)"
}

$ignoredChecks = @(".env", ".env.local", "appsettings.json", "*.pfx", "*.snk")
foreach ($candidate in $ignoredChecks) {
    & git -C $root check-ignore -q $candidate
    if ($LASTEXITCODE -ne 0) {
        throw "Expected sensitive file pattern is not ignored: $candidate"
    }
}

Write-Host "Repository privacy/secret scan passed."

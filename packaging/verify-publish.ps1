param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,
    [switch]$AllowPortableAppSettings,
    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Get-SpeakRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BasePath,
        [Parameter(Mandatory = $true)]
        [string]$TargetPath
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)
    $baseUri = [Uri]::new($baseFullPath)
    $targetUri = [Uri]::new($targetFullPath)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace(
        [System.IO.Path]::AltDirectorySeparatorChar,
        [System.IO.Path]::DirectorySeparatorChar)
}

$root = [System.IO.Path]::GetFullPath($PublishRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Publish directory does not exist: $root"
}

$requiredFiles = @(
    "Speak.exe",
    "Speak.deps.json",
    "Speak.runtimeconfig.json",
    "LICENSE",
    "NOTICE",
    "appsettings.template.json",
    "tools\speak_worker.py",
    "tools\whisper_resident_server.py",
    "tools\qwen3-tts\qwen3_tts_worker.py"
)

foreach ($relative in $requiredFiles) {
    $candidate = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Published artifact is missing required file: $relative"
    }
}

$forbiddenFiles = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object {
        $_.Name -match '(^\.env($|\.)|\.py[co]$|\.pyd$|\.pdb$|\.user$|\.bak($|-))' -or
        ($_.DirectoryName -like (Join-Path $root "tools*") -and
            $_.Extension -in @(".exe", ".dll"))
    }
if ($forbiddenFiles) {
    $relative = $forbiddenFiles |
        ForEach-Object { Get-SpeakRelativePath -BasePath $root -TargetPath $_.FullName }
    throw "Published artifact contains forbidden local/build files: $($relative -join ', ')"
}

$settingsPath = Join-Path $root "appsettings.json"
if ($AllowPortableAppSettings) {
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "The packaged installer stage must contain the audited portable appsettings.json."
    }
}
elseif (Test-Path -LiteralPath $settingsPath) {
    throw "A developer appsettings.json was copied into the clean publish."
}

$textExtensions = @(
    ".json", ".config", ".xml", ".txt", ".md", ".ps1", ".py", ".cmd", ".bat", ".yml", ".yaml"
)
$unixProfilePattern = '(?i)(/' + 'Users/[^/\s"]+|/' + 'home/[^/\s"]+)'
$privacyPatterns = [ordered]@{
    "Windows user profile path" = '(?i)[A-Z]:\\Users\\[^\\\s"]+'
    "Unix user profile path" = $unixProfilePattern
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

$findings = [System.Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.Extension -in $textExtensions }) {
    $relative = Get-SpeakRelativePath -BasePath $root -TargetPath $file.FullName
    $lines = @(Get-Content -LiteralPath $file.FullName)
    for ($index = 0; $index -lt $lines.Count; $index++) {
        foreach ($rule in $privacyPatterns.GetEnumerator()) {
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
    throw "Published artifact failed its privacy scan:`n$($findings -join [Environment]::NewLine)"
}

if (-not $SkipLaunch) {
    $executable = Join-Path $root "Speak.exe"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.WorkingDirectory = $root
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables["SPEAK_ENABLE_REST_API"] = "0"
    $smokeDataRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
        "Speak-smoke-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $smokeDataRoot -Force | Out-Null
    $startInfo.EnvironmentVariables["SPEAK_DATA_ROOT"] = $smokeDataRoot

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) {
            throw "Speak.exe did not start."
        }
        $started = $true

        if ($process.WaitForExit(5000)) {
            throw "Speak.exe exited during the five-second startup smoke test (exit code $($process.ExitCode))."
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            if ($PSVersionTable.PSVersion.Major -ge 7) {
                $process.Kill($true)
            }
            else {
                $process.Kill()
            }
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
        if (Test-Path -LiteralPath $smokeDataRoot) {
            Remove-Item -LiteralPath $smokeDataRoot -Recurse -Force
        }
    }
}

Write-Host "Publish verification passed: $root"

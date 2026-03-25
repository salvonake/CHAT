param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Title,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactName
)

if (-not $env:GITHUB_STEP_SUMMARY) {
    Write-Host "GITHUB_STEP_SUMMARY is not set. Skipping summary write."
    exit 0
}

$trx = Get-ChildItem -Path $ResultsDirectory -Filter *.trx -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $trx) {
    "## $Title`nNo TRX file found." | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
    exit 0
}

[xml]$xml = Get-Content $trx.FullName
$counters = $xml.TestRun.ResultSummary.Counters

"## $Title" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
"- Total: $($counters.total)" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
"- Passed: $($counters.passed)" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
"- Failed: $($counters.failed)" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
"- Artifact: $ArtifactName" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append

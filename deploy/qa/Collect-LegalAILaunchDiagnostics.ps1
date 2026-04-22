[CmdletBinding()]
param(
    [string]$OutputRoot,
    [int]$HoursBack = 24,
    [switch]$IncludeEventMessages
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path -Path $PSScriptRoot -ChildPath "evidence"
}

function New-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Export-Object {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, ValueFromPipeline = $true)]
        [object]$InputObject,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    process {
        $InputObject | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding UTF8
    }
}

function Copy-LatestLogs {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDir,
        [Parameter(Mandatory = $true)]
        [string]$Filter,
        [Parameter(Mandatory = $true)]
        [string]$DestinationDir,
        [int]$Count = 2
    )

    if (-not (Test-Path -LiteralPath $SourceDir)) {
        return @()
    }

    $files = Get-ChildItem -LiteralPath $SourceDir -Filter $Filter -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First $Count

    foreach ($file in $files) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $DestinationDir $file.Name) -Force
    }

    return $files
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputDir = Join-Path $OutputRoot "launch-triage-$timestamp"
New-Directory -Path $outputDir

$summary = [ordered]@{
    GeneratedAt = (Get-Date).ToString("o")
    ComputerName = $env:COMPUTERNAME
    UserName = [Environment]::UserName
    InstallDirRegistry = ""
    InstallDirExists = $false
    DesktopExePath = ""
    DesktopExeExists = $false
    AppSettingsExists = $false
    ShortcutTargetsValid = $false
    DotNetHostVersion = ""
    DotNetDesktopRuntime8Present = $false
    VCRedistInstalled = $false
    DesktopProcessState = "NotRunning"
    WorkerServicePresent = $false
    WorkerServiceStatus = "NotInstalled"
    DesktopLogsFolderExists = $false
    DesktopLogFilesFound = 0
    DesktopErrorLogFilesFound = 0
    EventEntriesCaptured = 0
    ProbableClassification = "Undetermined"
    NextAction = ""
}

$regPath = "HKLM:\Software\LegalAI"
if (Test-Path -LiteralPath $regPath) {
    $reg = Get-ItemProperty -LiteralPath $regPath
    if ($null -ne $reg.InstallDir) {
        $summary.InstallDirRegistry = [string]$reg.InstallDir
    }
}

$installDir = $summary.InstallDirRegistry
if ([string]::IsNullOrWhiteSpace($installDir)) {
    $installDir = Join-Path $env:ProgramFiles "LegalAI"
}

$summary.InstallDirExists = Test-Path -LiteralPath $installDir
$desktopExePath = Join-Path $installDir "LegalAI.Desktop.exe"
$summary.DesktopExePath = $desktopExePath
$summary.DesktopExeExists = Test-Path -LiteralPath $desktopExePath
$summary.AppSettingsExists = Test-Path -LiteralPath (Join-Path $installDir "appsettings.json")

$shell = New-Object -ComObject WScript.Shell
$shortcutScanLocations = @(
    [Environment]::GetFolderPath("Desktop"),
    "$env:PUBLIC\Desktop",
    "$env:ProgramData\Microsoft\Windows\Start Menu\Programs",
    "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
)

$shortcuts = foreach ($location in $shortcutScanLocations) {
    if (-not (Test-Path -LiteralPath $location)) {
        continue
    }

    Get-ChildItem -LiteralPath $location -Filter *.lnk -File -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object {
            $link = $shell.CreateShortcut($_.FullName)
            if ([string]::IsNullOrWhiteSpace($link.TargetPath)) {
                return
            }

            if ($link.TargetPath -notmatch "LegalAI\.Desktop\.exe$") {
                return
            }

            [pscustomobject]@{
                LinkPath = $_.FullName
                TargetPath = $link.TargetPath
                WorkingDirectory = $link.WorkingDirectory
                TargetExists = Test-Path -LiteralPath $link.TargetPath
                WorkingDirectoryMatchesTargetParent = ((Split-Path -Path $link.TargetPath -Parent) -ieq $link.WorkingDirectory)
            }
        }
}

if ($shortcuts) {
    $shortcuts | Sort-Object LinkPath | Export-Csv -LiteralPath (Join-Path $outputDir "shortcut-audit.csv") -NoTypeInformation -Encoding UTF8
    $summary.ShortcutTargetsValid = ($shortcuts | Where-Object { $_.TargetExists -and $_.WorkingDirectoryMatchesTargetParent }).Count -ge 1
}
else {
    @() | Export-Csv -LiteralPath (Join-Path $outputDir "shortcut-audit.csv") -NoTypeInformation -Encoding UTF8
}

$dotNetHostRegPath = "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost"
if (Test-Path -LiteralPath $dotNetHostRegPath) {
    $dotNetHost = Get-ItemProperty -LiteralPath $dotNetHostRegPath
    if ($null -ne $dotNetHost.Version) {
        $summary.DotNetHostVersion = [string]$dotNetHost.Version
    }
}

$dotnetExe = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (Test-Path -LiteralPath $dotnetExe) {
    $runtimeList = & $dotnetExe --list-runtimes 2>$null
    $runtimeList | Set-Content -LiteralPath (Join-Path $outputDir "dotnet-runtimes.txt") -Encoding UTF8
    $summary.DotNetDesktopRuntime8Present = ($runtimeList | Select-String -Pattern "^Microsoft\.WindowsDesktop\.App\s+8\.") -ne $null
}
else {
    "dotnet.exe not found at $dotnetExe" | Set-Content -LiteralPath (Join-Path $outputDir "dotnet-runtimes.txt") -Encoding UTF8
}

$vcRedistRegPath = "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"
if (Test-Path -LiteralPath $vcRedistRegPath) {
    $vc = Get-ItemProperty -LiteralPath $vcRedistRegPath
    $installedValue = 0
    if ($null -ne $vc.Installed) {
        $installedValue = [int]$vc.Installed
    }

    $summary.VCRedistInstalled = ($installedValue -eq 1)
    [pscustomobject]@{
        Installed = $installedValue
        Version = [string]$vc.Version
    } | Export-Object -Path (Join-Path $outputDir "vc-redist-status.json")
}
else {
    [pscustomobject]@{
        Installed = 0
        Version = ""
        Notes = "Registry key not found"
    } | Export-Object -Path (Join-Path $outputDir "vc-redist-status.json")
}

$desktopProcesses = Get-CimInstance -ClassName Win32_Process -Filter "Name='LegalAI.Desktop.exe'" -ErrorAction SilentlyContinue
$processRows = foreach ($proc in $desktopProcesses) {
    $mainWindowHandle = 0
    $mainWindowTitle = ""

    try {
        $dotNetProc = Get-Process -Id ([int]$proc.ProcessId) -ErrorAction Stop
        $mainWindowHandle = [long]$dotNetProc.MainWindowHandle
        $mainWindowTitle = [string]$dotNetProc.MainWindowTitle
    }
    catch {
        $mainWindowHandle = 0
    }

    [pscustomobject]@{
        ProcessId = [int]$proc.ProcessId
        ExecutablePath = [string]$proc.ExecutablePath
        CommandLine = [string]$proc.CommandLine
        CreationDate = [string]$proc.CreationDate
        MainWindowHandle = $mainWindowHandle
        MainWindowTitle = $mainWindowTitle
    }
}

if ($processRows) {
    $processRows | Export-Csv -LiteralPath (Join-Path $outputDir "desktop-processes.csv") -NoTypeInformation -Encoding UTF8
    if (($processRows | Where-Object { $_.MainWindowHandle -gt 0 }).Count -gt 0) {
        $summary.DesktopProcessState = "RunningWithWindow"
    }
    else {
        $summary.DesktopProcessState = "RunningNoVisibleWindow"
    }
}
else {
    @() | Export-Csv -LiteralPath (Join-Path $outputDir "desktop-processes.csv") -NoTypeInformation -Encoding UTF8
    $summary.DesktopProcessState = "NotRunning"
}

$service = Get-Service -Name "LegalAI WorkerService" -ErrorAction SilentlyContinue
if ($null -ne $service) {
    $summary.WorkerServicePresent = $true
    $summary.WorkerServiceStatus = [string]$service.Status

    [pscustomobject]@{
        Name = $service.Name
        DisplayName = $service.DisplayName
        Status = [string]$service.Status
        StartType = (Get-CimInstance -ClassName Win32_Service -Filter "Name='LegalAI WorkerService'" -ErrorAction SilentlyContinue).StartMode
    } | Export-Object -Path (Join-Path $outputDir "worker-service.json")
}
else {
    [pscustomobject]@{
        Name = "LegalAI WorkerService"
        Status = "NotInstalled"
    } | Export-Object -Path (Join-Path $outputDir "worker-service.json")
}

$desktopLogDir = Join-Path $env:LOCALAPPDATA "LegalAI\Logs"
$summary.DesktopLogsFolderExists = Test-Path -LiteralPath $desktopLogDir
if ($summary.DesktopLogsFolderExists) {
    $desktopLogs = Get-ChildItem -LiteralPath $desktopLogDir -Filter "legalai-*.log" -File -ErrorAction SilentlyContinue
    $desktopErrorLogs = Get-ChildItem -LiteralPath $desktopLogDir -Filter "legalai-errors-*.log" -File -ErrorAction SilentlyContinue

    $summary.DesktopLogFilesFound = @($desktopLogs).Count
    $summary.DesktopErrorLogFilesFound = @($desktopErrorLogs).Count

    New-Directory -Path (Join-Path $outputDir "logs")
    Copy-LatestLogs -SourceDir $desktopLogDir -Filter "legalai-*.log" -DestinationDir (Join-Path $outputDir "logs") | Out-Null
    Copy-LatestLogs -SourceDir $desktopLogDir -Filter "legalai-errors-*.log" -DestinationDir (Join-Path $outputDir "logs") | Out-Null
}

$workerInstanceRoot = Join-Path $env:LOCALAPPDATA "LegalAI\Instances"
if (Test-Path -LiteralPath $workerInstanceRoot) {
    Get-ChildItem -LiteralPath $workerInstanceRoot -Directory -ErrorAction SilentlyContinue |
        Select-Object FullName, LastWriteTime |
        Export-Csv -LiteralPath (Join-Path $outputDir "worker-instance-directories.csv") -NoTypeInformation -Encoding UTF8
}
else {
    @() | Export-Csv -LiteralPath (Join-Path $outputDir "worker-instance-directories.csv") -NoTypeInformation -Encoding UTF8
}

$startTime = (Get-Date).AddHours(-1 * [math]::Abs($HoursBack))
$appEvents = Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = $startTime } -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ProviderName -in @(".NET Runtime", "Application Error", "Windows Error Reporting") -and
        $_.Message -match "LegalAI|LegalAI\.Desktop"
    }

$summary.EventEntriesCaptured = @($appEvents).Count

if ($IncludeEventMessages) {
    $appEvents |
        Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, MachineName, Message |
        Export-Csv -LiteralPath (Join-Path $outputDir "eventlog-application.csv") -NoTypeInformation -Encoding UTF8
}
else {
    $appEvents |
        Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, MachineName |
        Export-Csv -LiteralPath (Join-Path $outputDir "eventlog-application.csv") -NoTypeInformation -Encoding UTF8
}

if (-not $summary.DesktopExeExists) {
    $summary.ProbableClassification = "InstallPayloadMissing"
    $summary.NextAction = "Re-run installer repair and validate InstallDir + shortcut target."
}
elseif (-not $summary.DotNetDesktopRuntime8Present) {
    $summary.ProbableClassification = "PrerequisiteMissingDotNetDesktop8"
    $summary.NextAction = "Install .NET Desktop Runtime 8 x64 and retry launch."
}
elseif (-not $summary.VCRedistInstalled) {
    $summary.ProbableClassification = "PrerequisiteMissingVCRedist"
    $summary.NextAction = "Install VC++ Redistributable x64 and retry launch."
}
elseif ($summary.DesktopProcessState -eq "RunningNoVisibleWindow") {
    $summary.ProbableClassification = "RunningButHiddenOrTray"
    $summary.NextAction = "Restore from system tray, then validate minimize-to-tray settings and relaunch behavior."
}
elseif ($summary.DesktopProcessState -eq "NotRunning") {
    $summary.ProbableClassification = "LaunchFailedBeforeSteadyState"
    $summary.NextAction = "Launch executable directly and correlate app logs with Event Viewer entries."
}
else {
    $summary.ProbableClassification = "RunningWithVisibleWindow"
    $summary.NextAction = "Proceed to full functional smoke tests and release checklist."
}

$summaryObject = [pscustomobject]$summary
$summaryObject | Export-Object -Path (Join-Path $outputDir "summary.json")
$summaryObject | Export-Csv -LiteralPath (Join-Path $outputDir "summary.csv") -NoTypeInformation -Encoding UTF8
$summaryObject | Format-List | Out-String | Set-Content -LiteralPath (Join-Path $outputDir "summary.txt") -Encoding UTF8

Write-Host "Diagnostics complete." -ForegroundColor Green
Write-Host "Output folder: $outputDir"
Write-Host "Classification: $($summary.ProbableClassification)"
Write-Host "Next action: $($summary.NextAction)"

# LegalAI Post-Install Launch QA Runbook

## Purpose

Use this runbook when installation completes but launching from desktop shortcut appears to do nothing. It provides:

- Deterministic triage in under 5 minutes
- Full root-cause isolation (shortcut, hidden/tray, crash, prereq, permissions)
- Evidence collection for release sign-off
- A pass/fail worksheet aligned to test IDs

Companion files:

- Diagnostics script: `deploy/qa/Collect-LegalAILaunchDiagnostics.ps1`
- Pass/fail sheet: `deploy/qa/Launch-Issue-PassFail-Template.csv`

## Prerequisites

Run all commands in elevated PowerShell unless noted.

```powershell
Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
```

## Phase 1: 5-Minute Triage

### TRI-01 Run automated diagnostics

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\qa\Collect-LegalAILaunchDiagnostics.ps1 -HoursBack 24 -IncludeEventMessages
```

Expected:

- Script prints output folder and a classification.
- Folder contains summary.json, shortcut-audit.csv, eventlog-application.csv.

### TRI-02 Read classification immediately

```powershell
$latest = Get-ChildItem .\deploy\qa\evidence -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content (Join-Path $latest.FullName "summary.txt")
```

Decision map:

- InstallPayloadMissing: installer payload issue or wrong install path.
- PrerequisiteMissingDotNetDesktop8: missing .NET Desktop Runtime 8 x64.
- PrerequisiteMissingVCRedist: missing VC++ x64 runtime.
- RunningButHiddenOrTray: instance exists, UI likely hidden/minimized to tray.
- LaunchFailedBeforeSteadyState: process exits or never stabilizes.
- RunningWithVisibleWindow: launch path is healthy; proceed to full functional checks.

## Phase 2: Installer and Shortcut Verification

### INS-01 Verify installed path from registry

```powershell
Get-ItemProperty -Path "HKLM:\Software\LegalAI" | Select-Object InstallDir, Version, ModelsDir | Format-List
```

Expected:

- InstallDir points to Program Files\LegalAI.
- Version is populated.

### INS-02 Verify desktop executable and critical files

```powershell
$installDir = (Get-ItemProperty -Path "HKLM:\Software\LegalAI").InstallDir
Test-Path (Join-Path $installDir "LegalAI.Desktop.exe")
Test-Path (Join-Path $installDir "appsettings.json")
Get-ChildItem $installDir | Select-Object Name, Length, LastWriteTime
```

Expected:

- LegalAI.Desktop.exe exists.
- appsettings.json exists.

### INS-03 Audit all LegalAI shortcuts

```powershell
$shell = New-Object -ComObject WScript.Shell
$roots = @([Environment]::GetFolderPath("Desktop"), "$env:PUBLIC\Desktop", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs")
foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem -Path $root -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        $lnk = $shell.CreateShortcut($_.FullName)
        if ($lnk.TargetPath -match "LegalAI\.Desktop\.exe$") {
            [pscustomobject]@{
                Link = $_.FullName
                Target = $lnk.TargetPath
                WorkingDirectory = $lnk.WorkingDirectory
                TargetExists = Test-Path $lnk.TargetPath
                WorkingDirMatchesTargetParent = ((Split-Path $lnk.TargetPath -Parent) -ieq $lnk.WorkingDirectory)
            }
        }
    }
} | Format-Table -AutoSize
```

Expected:

- At least one desktop/start menu shortcut points to LegalAI.Desktop.exe.
- WorkingDirectory matches the executable folder.

## Phase 3: Dependency and Environment Validation

### ENV-01 Verify .NET runtime

```powershell
Get-ItemProperty -Path "HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost" | Select-Object Version
& "$env:ProgramFiles\dotnet\dotnet.exe" --list-runtimes | Select-String "Microsoft.WindowsDesktop.App 8."
```

Expected:

- Shared host version present.
- Microsoft.WindowsDesktop.App 8.x listed.

### ENV-02 Verify VC++ runtime

```powershell
Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64" | Select-Object Installed, Version
```

Expected:

- Installed = 1.

### ENV-03 Check policy/permission blockers

```powershell
$exe = Join-Path ((Get-ItemProperty -Path "HKLM:\Software\LegalAI").InstallDir) "LegalAI.Desktop.exe"
Get-Item $exe | Select-Object FullName, Attributes, LastWriteTime
Get-MpThreatDetection -ErrorAction SilentlyContinue | Select-Object InitialDetectionTime, ThreatName, Resources
```

Expected:

- EXE is accessible and not quarantined.

## Phase 4: Launch State Differentiation

### LCH-01 Launch from shortcut, then classify process state

```powershell
Start-Process "explorer.exe" "shell:Desktop"
# double-click the LegalAI shortcut now, then run the next block
Get-CimInstance Win32_Process -Filter "Name='LegalAI.Desktop.exe'" | Select-Object ProcessId, ExecutablePath, CommandLine, CreationDate
Get-Process -Name "LegalAI.Desktop" -ErrorAction SilentlyContinue | Select-Object Id, MainWindowHandle, MainWindowTitle, StartTime
```

Interpretation:

- No process: shortcut/dependency/blocking issue.
- Process with MainWindowHandle = 0: likely hidden/tray or pre-window failure.
- Process with MainWindowHandle > 0: window exists; likely off-screen/focus issue.

### LCH-02 Bypass shortcut and launch EXE directly

```powershell
$exe = Join-Path ((Get-ItemProperty -Path "HKLM:\Software\LegalAI").InstallDir) "LegalAI.Desktop.exe"
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent)
```

Expected:

- If direct EXE works and shortcut does not, shortcut metadata is wrong.
- If both fail similarly, root cause is runtime/environment.

### LCH-03 Hidden/tray validation

Manual actions:

- Check notification area for LegalAI icon.
- Double-click tray icon to restore.
- Right-click tray icon and use Open LegalAI.

Expected:

- Existing instance restores and focuses.

## Phase 5: Log and Event Correlation

### LOG-01 Inspect application logs

```powershell
$logDir = Join-Path $env:LOCALAPPDATA "LegalAI\Logs"
Get-ChildItem $logDir -Filter "legalai*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 6 Name, LastWriteTime, Length
Get-Content (Get-ChildItem $logDir -Filter "legalai-errors-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName -Tail 200
```

Expected:

- Startup attempts are logged.
- Any fatal exception appears with stack trace.

### LOG-02 Inspect Windows Application events

```powershell
$start = (Get-Date).AddHours(-24)
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$start} |
    Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error','Windows Error Reporting') -and $_.Message -match 'LegalAI|LegalAI.Desktop' } |
    Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message |
    Format-List
```

Expected:

- Matching error timestamps confirm crash timing and source.

## Phase 6: Service Context Validation

### SRV-01 Validate worker service state (if relevant to deployment profile)

```powershell
Get-Service -Name "LegalAI WorkerService" -ErrorAction SilentlyContinue | Select-Object Name, DisplayName, Status
Get-CimInstance Win32_Service -Filter "Name='LegalAI WorkerService'" | Select-Object Name, StartMode, State, PathName
```

Expected:

- Service presence/status matches intended deployment mode.
- Do not treat missing service as launcher defect unless this build is supposed to install and run it.

### SRV-02 Validate worker instance directories

```powershell
Get-ChildItem "$env:LOCALAPPDATA\LegalAI\Instances" -Directory -ErrorAction SilentlyContinue | Select-Object FullName, LastWriteTime
```

Expected:

- Instance-scoped data appears when worker has run.

## Phase 7: Functional Smoke After Launch Recovery

### SMK-01 Shell visibility and navigation

Manual checks:

- Main window opens and remains visible.
- Navigate Ask, Chat, Documents, Health, Settings.

### SMK-02 First-run/setup behavior

Manual checks:

- If models are missing, setup flow is shown.
- If models are present, shell reaches ready state.

### SMK-03 Background lifecycle

Manual checks:

- Minimize and close behavior follows configured tray settings.
- Relaunch restores existing instance instead of spawning duplicates.

## Phase 8: Release Gate Criteria

All must pass:

- TRI, INS, ENV, LCH, LOG mandatory tests are PASS.
- No unexplained startup crash in latest 24-hour logs/events.
- Shortcut target and working directory are valid.
- Hidden/tray behavior is recoverable and user-discoverable.
- Smoke tests pass on clean install and upgrade path.

## Evidence Packaging

Create one evidence folder per test machine and store:

- Diagnostics output folder from script
- Screenshots or short capture of launch and tray recovery
- Completed pass/fail CSV
- Notes on machine policy context (AV/GPO/AppLocker)

## Pass/Fail Recording

Use `deploy/qa/Launch-Issue-PassFail-Template.csv` and set Status to PASS, FAIL, or BLOCKED for each TestId.

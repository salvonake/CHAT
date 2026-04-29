# Poseidon Launch Issue Runbook

Use this runbook for installed desktop launch, provisioning, and Recovery-mode incidents.

## Evidence Collection

Run:

```powershell
.\deploy\qa\Collect-PoseidonLaunchDiagnostics.ps1
```

Collect:

- `%LOCALAPPDATA%\Poseidon\Logs\startup.log`
- latest `poseidon-*.log` and `poseidon-errors-*.log`
- Windows Application event log entries for `Poseidon.Desktop.exe`
- installed config at `%ProgramFiles%\Poseidon\appsettings.user.json`
- installed model manifest at `%ProgramFiles%\Poseidon\Models\model-manifest.json`
- ProvisioningCheck output

## First Checks

1. Confirm installer source is `Poseidon.Installer.msi` or `Poseidon.Bundle.exe`.
2. Confirm `Poseidon.Desktop.exe` exists under the installed directory.
3. Confirm `appsettings.user.json` is valid JSON.
4. Confirm `model-manifest.json` exists and contains SHA-256 hashes.
5. Run `provisioning-check.exe` against installed config and manifest.
6. Confirm runtime state is Full, Degraded, or Recovery; undefined startup states are release blockers.

## Recovery Classification

Recovery is expected when:

- required model files are missing;
- model hashes do not match;
- configured provider requirements are incomplete;
- DPAPI secret references are missing or corrupt;
- config JSON is invalid;
- production security policy is disabled or incomplete.

Recovery is not expected for a valid full installer deployment.

## Installer Support Path

For install failures, capture MSI/Burn logs:

```powershell
msiexec /i Poseidon.Installer.msi /L*v poseidon-msi-install.log
.\Poseidon.Bundle.exe /quiet /norestart /log poseidon-burn-install.log
```

Repair must rerun provisioning validation. If repair succeeds but first launch enters Recovery, treat the installed config/model manifest as suspect.

## Pass Criteria

- Desktop launches without setup wizard after a valid full install.
- Hosted ingestion services do not start before provisioning is complete.
- Full/degraded/recovery state is deterministic and logged.
- Diagnostics remain available in Recovery.
- No plaintext production secrets appear in config or logs.

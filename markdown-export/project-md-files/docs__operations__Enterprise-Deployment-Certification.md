# Poseidon Enterprise Deployment Certification

## Required Deployment Channels

Poseidon enterprise certification requires evidence for:

- interactive install
- silent MSI install
- silent Burn install
- repair
- upgrade
- rollback
- uninstall
- SCCM deployment
- Intune deployment
- GPO deployment
- SYSTEM-context deployment
- multi-user machine deployment
- existing user migration
- existing config preservation
- external model mode
- SCCM external payload delivery
- Intune payload staging
- GPO network payload access
- SYSTEM-context payload resolution
- offline external payload install
- partial payload failure
- rollback after payload copy failure
- upgrade with existing payload
- repair with missing payload
- multi-user external payload
- full local mode
- degraded mode

## Evidence Format

Deployment labs submit `deploy/enterprise/deployment-evidence.json` with one entry per scenario:

```json
{
  "schemaVersion": 1,
  "scenarios": [
    {
      "id": "silent-msi-install",
      "status": "passed",
      "installResult": "0",
      "provisioningResult": "passed",
      "startupMode": "Full",
      "repairResult": "passed",
      "rollbackResult": "not-applicable",
      "uninstallPolicy": "machine removed, user data preserved",
      "logArtifact": "evidence/silent-msi-install.log"
    }
  ]
}
```

Run:

```powershell
./tests/scripts/Test-EnterpriseDeploymentEvidence.ps1 -RequireCertified
```

Certification fails closed if any required scenario is missing, failed, or lacks log evidence.

## External Payload Certification

Full local installs that externalize the LLM must attach the matrix in `docs/release/External-Payload-Deployment-Matrix.md`. Deployment evidence must prove:

- `Setup.exe` and `payload\Models` are delivered as a single deployment unit.
- SYSTEM-context installs resolve the same payload root expected by Burn.
- payload filename, size, and SHA-256 match `model-manifest.json`.
- missing or corrupted payloads fail installation and do not produce trusted Full startup state.
- repair and upgrade behavior is deterministic when the payload already exists or must be restored.

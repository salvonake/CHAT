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


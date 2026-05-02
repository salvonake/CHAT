# External Payload Deployment Matrix

Poseidon full local mode may externalize production-scale LLM files when a model exceeds Windows Installer single-file limits. The Burn bootstrapper remains the authoritative installer. The external payload is release-controlled only when it is distributed beside the bundle under the expected payload layout and its SHA-256 matches `model-manifest.json`.

## Authoritative Layout

| Artifact | Required location | Certification requirement |
|---|---|---|
| Burn bundle | `Setup.exe` or `Poseidon.Bundle.exe` | Signed production bootstrapper, launched as the deployment entry point |
| MSI | embedded or chained by the bundle | Signed production MSI, not used alone for external LLM deployment |
| LLM payload | `payload\Models\<manifest filename>` beside the bundle | Hash must match the manifest before installation continues |
| Manifest | `model-manifest.json` | Contains target path, size, mode, and SHA-256 for every required model |
| Evidence | deployment lab logs | One passed scenario entry per required external payload gate |

## Certification Scenarios

| Scenario ID | Validation objective | Required evidence |
|---|---|---|
| `sccm-external-payload-delivery` | SCCM distributes `Setup.exe` and `payload\Models` without separating the model payload from the bundle source folder. | SCCM content layout, install command, exit code, payload hash verification, provisioning log |
| `intune-payload-staging` | Intune package preserves the payload folder after extraction or staging. | Intune package manifest, detection rule, install log, payload path used by SYSTEM, provisioning log |
| `gpo-network-payload-access` | GPO install can read the payload from the approved network distribution point. | UNC path ACL proof, machine account access proof, install log, hash verification log |
| `system-context-payload-resolution` | SYSTEM-context installation resolves `[WixBundleOriginalSourceFolder]payload\Models` deterministically. | SYSTEM install transcript, resolved payload path, model helper exit code |
| `offline-external-payload-install` | Full installation succeeds with no internet access when prerequisites and payload are locally staged. | Offline network state, staged prerequisite proof, bundle log, provisioning log |
| `partial-payload-failure` | Missing, truncated, or hash-mismatched payload blocks install success. | Negative install log, nonzero helper or bundle result, no trusted Full startup state |
| `rollback-after-payload-copy-failure` | Failure during payload copy does not certify a partial model as trusted. | Failure log, target model absence or hash mismatch quarantine proof, rollback result |
| `upgrade-existing-payload` | Upgrade with an already valid installed LLM does not recopy unnecessarily and preserves manifest continuity. | Previous/current manifest hashes, upgrade log, provisioning log |
| `repair-missing-payload` | Repair restores a missing LLM only when the original external payload source is available and hash-valid. | Repair log, payload source proof, restored file hash |
| `multi-user-external-payload` | First and later users on the same machine observe the same machine-level model trust state. | First-user and second-user startup logs, shared install path proof |

## Fail-Closed Rules

External payload deployment is not certified when:

- `Setup.exe` is copied without its matching `payload\Models` directory.
- the model filename differs from `model-manifest.json`.
- the payload hash differs from the manifest hash.
- deployment tooling stages the bundle and payload in different source roots.
- the MSI is invoked directly for full local mode that requires an external LLM payload.
- SYSTEM cannot read the payload source.
- repair or upgrade succeeds while provisioning validation fails.

## Evidence Contract

`tests/scripts/Test-EnterpriseDeploymentEvidence.ps1 -RequireCertified` requires every external payload scenario above. Each entry in `deploy/enterprise/deployment-evidence.json` must include `status: "passed"` and a non-empty `logArtifact`.

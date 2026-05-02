# Poseidon Enterprise Validation Matrix

## Repository

| Gate | Required Result |
|---|---|
| Active source tracked | All `Poseidon.*` production files tracked |
| Legacy predecessor files | No operational predecessor files tracked |
| Generated artifacts | No tracked publish, installer output, test result, evidence, or cache files |
| Repository hygiene | `tests/scripts/Test-RepositoryHygiene.ps1` passes |
| Release evidence | `tests/scripts/New-ReleaseEvidence.ps1 -FailOnDirty` passes |

## Build

| Gate | Required Result |
|---|---|
| Restore | `dotnet restore Poseidon.sln` passes |
| Build | `dotnet build Poseidon.sln -c Release --no-restore` passes |
| Unit tests | Unit test project passes in Release |
| SDK | Local, CI, and docs use the pinned SDK |
| Packages | No floating package versions |
| Package locks | Every project has a tracked `packages.lock.json` and locked restore passes |

## Installer

| Gate | Required Result |
|---|---|
| Full mode | Bundled LLM and embedding present with hashes |
| Full mode with external LLM payload | `Setup.exe` and `payload\Models` layout present; LLM hash matches manifest before install succeeds |
| Degraded mode | Embedding present, LLM intentionally external or omitted |
| External mode | External provider endpoints and model identifiers validated |
| Post-install validation | `provisioning-check` blocks failed installs |
| Silent MSI | install, repair, uninstall pass |
| Silent Burn | install, repair, uninstall pass |
| Upgrade | previous bundle to current bundle pass |
| Tamper repair | corrupted model or config is repaired or blocked deterministically |

## Security

| Gate | Required Result |
|---|---|
| JWT secrets | no placeholders or weak production secrets |
| Management keys | no empty or placeholder production keys |
| Secret storage | production config uses protected secret references |
| Model integrity | required model hashes present and enforced |
| Prerequisites | payloads are hash pinned |
| Signing | MSI, Burn bundle, and provisioning-check signatures are valid |
| Compliance | SBOM, dependency inventory, artifact checksums, and final certification report are generated |

## Enterprise Deployment

| Gate | Required Result |
|---|---|
| SCCM/system context | silent install succeeds without user profile assumptions |
| GPO deployment | machine install succeeds and first launch is deterministic |
| Multi-user | first user and subsequent users enter expected startup state |
| Uninstall | policy-approved user data retention behavior is honored |
| Rollback | failed upgrade rolls back without leaving partial trusted config |
| SCCM external payload | bundle and `payload\Models` are deployed as one content unit |
| Intune payload staging | package extraction preserves the payload folder for SYSTEM |
| GPO network payload | machine account can read payload from approved distribution path |
| Offline payload install | install succeeds without internet when prerequisites and payload are staged |
| Payload failure handling | missing, truncated, or hash-mismatched payload blocks certification |
| Repair/upgrade payload behavior | existing valid payload is preserved; missing payload repair requires valid source |

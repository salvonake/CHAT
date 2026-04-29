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

## Installer

| Gate | Required Result |
|---|---|
| Full mode | Bundled LLM and embedding present with hashes |
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

## Enterprise Deployment

| Gate | Required Result |
|---|---|
| SCCM/system context | silent install succeeds without user profile assumptions |
| GPO deployment | machine install succeeds and first launch is deterministic |
| Multi-user | first user and subsequent users enter expected startup state |
| Uninstall | policy-approved user data retention behavior is honored |
| Rollback | failed upgrade rolls back without leaving partial trusted config |


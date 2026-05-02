# Poseidon Enterprise Deployment

Poseidon production deployment uses the official WiX/Burn installer pipeline. Manual source setup is for development only.

## Artifacts

- `Poseidon.Installer.msi`: enterprise MSI for SCCM/GPO-style deployment.
- `Poseidon.Bundle.exe`: Burn bundle that chains prerequisites and the MSI.
- `Setup.exe`: release-friendly copy of the Burn bundle.
- `payload/Models/<LLM model>`: externalized production LLM payload when the model is too large for MSI packaging.
- `model-manifest.json`: schema-versioned model manifest with file names, target paths, sizes, modes, and SHA-256 hashes.
- `build-provenance.json`: release provenance, signing, prerequisite, and secret-storage metadata.

## Install Modes

- Full: LLM and embedding models are bundled and hash-validated.
- Degraded: embedding model is bundled and LLM omission is explicit.
- External model mode: model references are validated by contract and must not bypass hash or provider policy.
- Full with external LLM payload: `Setup.exe` installs the application and prerequisites, then copies the LLM from `payload/Models` into the machine install path with SHA-256 validation.

## External Payload Distribution

When full local mode uses an external LLM payload, enterprise deployment tools must preserve this layout:

```text
Setup.exe
payload\Models\<LLM filename from model-manifest.json>
```

SCCM, Intune, GPO, offline media, and network-share deployments must prove that the bundle and payload remain co-located for the installing account, including SYSTEM. Direct MSI deployment is valid only for modes that do not require an external LLM payload.

## Production Requirements

- Signed MSI, Burn bundle, and provisioning-check executable.
- Signed payload helper executable in production builds.
- Pinned prerequisite hashes.
- External payload hash continuity with `model-manifest.json`.
- Machine config with DPAPI secret references, not plaintext production secrets.
- Successful post-install provisioning-check.
- First launch must enter Full, Degraded, or Recovery deterministically.

## Validation

Run:

```powershell
dotnet restore Poseidon.sln
dotnet build Poseidon.sln -c Release --no-restore
dotnet test tests/Poseidon.UnitTests/Poseidon.UnitTests.csproj -c Release --no-build
.\installer\Validate-InstallerArtifacts.ps1 -BuildProfile Production
```

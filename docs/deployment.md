# Poseidon Enterprise Deployment

Poseidon production deployment uses the official WiX/Burn installer pipeline. Manual source setup is for development only.

## Artifacts

- `Poseidon.Installer.msi`: enterprise MSI for SCCM/GPO-style deployment.
- `Poseidon.Bundle.exe`: Burn bundle that chains prerequisites and the MSI.
- `model-manifest.json`: schema-versioned model manifest with file names, target paths, sizes, modes, and SHA-256 hashes.
- `build-provenance.json`: release provenance, signing, prerequisite, and secret-storage metadata.

## Install Modes

- Full: LLM and embedding models are bundled and hash-validated.
- Degraded: embedding model is bundled and LLM omission is explicit.
- External model mode: model references are validated by contract and must not bypass hash or provider policy.

## Production Requirements

- Signed MSI, Burn bundle, and provisioning-check executable.
- Pinned prerequisite hashes.
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

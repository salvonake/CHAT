# Poseidon Release Checklist

## Preparation

- [ ] Confirm release version uses SemVer.
- [ ] Confirm release branch policy is satisfied.
- [ ] Confirm production model assets are staged outside git.
- [ ] Confirm signing certificate is available and valid.
- [ ] Confirm timestamp service is reachable.
- [ ] Confirm prerequisite hashes match `installer/prerequisites.json`.

## Repository Proof

- [ ] `git status` is clean.
- [ ] All active `Poseidon.*` files are tracked.
- [ ] No operational predecessor files are tracked.
- [ ] No generated artifacts are tracked.
- [ ] `tests/scripts/Test-RepositoryHygiene.ps1` passes.
- [ ] `tests/scripts/New-ReleaseEvidence.ps1 -FailOnDirty` passes.

## Build Proof

- [ ] `dotnet restore Poseidon.sln --locked-mode` passes.
- [ ] `dotnet build Poseidon.sln -c Release --no-restore` passes.
- [ ] `dotnet test tests/Poseidon.UnitTests/Poseidon.UnitTests.csproj -c Release --no-build` passes.
- [ ] Clean clone validation passes.

## Installer Proof

- [ ] `installer/build-installer.ps1 -BuildProfile Production` passes with production assets.
- [ ] `installer/Validate-InstallerArtifacts.ps1 -BuildProfile Production` passes.
- [ ] staged `provisioning-check.exe` validation passes.
- [ ] MSI silent install, repair, and uninstall pass.
- [ ] Burn silent install, repair, and uninstall pass.
- [ ] Upgrade from previous release passes.
- [ ] Tamper-repair test passes.

## Signing Proof

- [ ] `provisioning-check.exe` is signed.
- [ ] `Poseidon.Installer.msi` is signed.
- [ ] `Poseidon.Bundle.exe` is signed.
- [ ] Authenticode verification is valid.
- [ ] signing report contains signer subject, thumbprint, and timestamp URL.

## Promotion

- [ ] Artifact checksums are published.
- [ ] SBOM or dependency inventory is attached.
- [ ] Compliance package is attached.
- [ ] Enterprise deployment certification matrix is attached.
- [ ] Final enterprise certification report is attached.
- [ ] Compliance metadata is attached.
- [ ] Release owner approves promotion.
- [ ] Deployment owner approves enterprise rollout.

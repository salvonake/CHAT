# Poseidon Production Signing Runbook

## Purpose

This runbook defines the production signing procedure for Poseidon release artifacts.

## Required Inputs

- Windows runner with Windows SDK and `signtool.exe`.
- Organization-approved code-signing certificate.
- Certificate thumbprint or encrypted PFX secret.
- Timestamp authority URL.
- Production model asset package.
- Production encryption passphrase injection secret.

## Required Signed Artifacts

- `Poseidon.Installer.msi`
- `Poseidon.Bundle.exe`
- `Setup.exe`
- `provisioning-check.exe`
- `ModelPayloadInstaller.exe` when external LLM payload mode is used.
- `Poseidon.Desktop.exe` when desktop executable signing is enabled.

`Setup.exe` is the enterprise entry point and must retain a valid Authenticode
signature after being copied from the Burn bundle. External payload mode also
requires the helper executable to be signed before it is embedded in the bundle.

## Validation

Run:

```powershell
./tests/scripts/Test-ReleaseSigningReadiness.ps1 -BuildProfile Production -SigningCertificateThumbprint "<thumbprint>"
./installer/build-installer.ps1 -BuildProfile Production -SigningCertificateThumbprint "<thumbprint>"
./installer/Validate-InstallerArtifacts.ps1 -BuildProfile Production
```

Production signing is not optional. Missing certificate, missing `signtool.exe`, failed timestamping, or invalid Authenticode status blocks release promotion.

## Evidence

Attach:

- `signing-report.json`
- `signing-readiness-report.json`
- certificate subject
- certificate thumbprint
- certificate validity period
- timestamp authority
- SHA-256 hashes for signed artifacts
- SHA-256 hashes for external payload files under `payload/Models`
- helper executable signature evidence when external payload mode is used

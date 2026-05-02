# Installer Contract

WiX MSI plus Burn bundle is the single supported enterprise installer architecture.

## Build Entry Point

```powershell
.\installer\build-installer.ps1 -Configuration Release -ModelsPath "C:\Poseidon\Models" -BuildProfile Production
```

## Official Outputs

- `installer/output/Poseidon.Installer.msi`
- `installer/output/Poseidon.Bundle.exe`
- `installer/output/Setup.exe`
- `installer/output/payload/Models/<LLM model>` when the LLM exceeds MSI single-file limits

## Machine Contract

The installer must create:

- `[INSTALLDIR]\Poseidon.Desktop.exe`
- `[INSTALLDIR]\provisioning-check.exe`
- `[INSTALLDIR]\appsettings.user.json`
- `[INSTALLDIR]\Models\model-manifest.json`
- required model files under `[INSTALLDIR]\Models`

`provisioning-check` runs after file installation and blocks install success when config, model, hash, provider, or secret contract validation fails.

## External LLM Payload Contract

Production-scale GGUF files can exceed the MSI single-file ceiling. In that case the Burn bundle remains the authoritative installer and the LLM is distributed as an external payload beside the bundle:

```text
installer/output/
  Setup.exe
  payload/
    Models/
      <LLM filename from model-manifest.json>
```

The payload helper copies the LLM into `[INSTALLDIR]\Models`, verifies SHA-256 against `model-manifest.json`, and fails closed when the payload is missing, truncated, renamed, or hash-mismatched. The MSI must not be used alone for full local mode when the manifest requires an external LLM payload.

Enterprise deployment systems must preserve the relative `Setup.exe` +
`payload\Models` layout. SCCM, Intune, GPO, offline media, and network-share
delivery packages are certification-blocked unless lab evidence proves this
layout survives staging, SYSTEM-context execution, repair, upgrade, rollback,
and multi-user lifecycle scenarios.

## Deprecated Paths

Inno and legacy installer identities are not supported production paths. Git history is the archive for removed migration files.

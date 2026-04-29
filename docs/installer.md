# Installer Contract

WiX MSI plus Burn bundle is the single supported enterprise installer architecture.

## Build Entry Point

```powershell
.\installer\build-installer.ps1 -Configuration Release -ModelsPath "C:\Poseidon\Models" -BuildProfile Production
```

## Official Outputs

- `installer/output/Poseidon.Installer.msi`
- `installer/output/Poseidon.Bundle.exe`

## Machine Contract

The installer must create:

- `[INSTALLDIR]\Poseidon.Desktop.exe`
- `[INSTALLDIR]\provisioning-check.exe`
- `[INSTALLDIR]\appsettings.user.json`
- `[INSTALLDIR]\Models\model-manifest.json`
- required model files under `[INSTALLDIR]\Models`

`provisioning-check` runs after file installation and blocks install success when config, model, hash, provider, or secret contract validation fails.

## Deprecated Paths

Inno and legacy installer identities are not supported production paths. Git history is the archive for removed migration files.

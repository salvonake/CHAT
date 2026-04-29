# Poseidon (LCDSS)

Poseidon is a Windows desktop legal decision support system for private document ingestion, Arabic-aware OCR/embedding workflows, semantic search, evidence-constrained legal querying, local AI model operation, runtime diagnostics, and enterprise deployment.

The production entry point is the WPF desktop application installed through the official WiX/Burn pipeline. The API, Worker Service, Management API, and ProvisioningCheck tools support desktop operation, fleet diagnostics, and release validation.

## Architecture

- `src/Poseidon.Desktop`: WPF desktop shell, first-launch provisioning, diagnostics, model health, ingestion UI, and runtime mode control.
- `src/Poseidon.Api`: JWT-secured ASP.NET Core API for legal queries, document workflows, and operational endpoints.
- `src/Poseidon.WorkerService`: Windows worker host for watched-folder ingestion.
- `src/Poseidon.Management.Api`: management plane for signed heartbeat and command workflows.
- `src/Poseidon.ProvisioningCheck`: installer/runtime validation executable.
- `src/Poseidon.Domain`, `Application`, `Ingestion`, `Retrieval`, `Infrastructure`, `Security`: shared layered business, model, storage, retrieval, and trust-boundary services.

## Runtime Modes

- `Full`: required LLM and embedding providers are configured, reachable, and integrity-valid.
- `Degraded`: explicitly declared deployment mode where local LLM is omitted but embeddings and retrieval remain valid.
- `Recovery`: config, model, provider, secret, or integrity validation failed; diagnostics remain available and normal hosted services are prevented from consuming unsafe state.

## Official Installer

The only authoritative production packaging path is WiX/Burn:

```powershell
.\installer\build-installer.ps1 -Configuration Release -ModelsPath "C:\Poseidon\Models" -BuildProfile Production
```

Official artifacts:

- `installer/output/Poseidon.Installer.msi`
- `installer/output/Poseidon.Bundle.exe`

The installer publishes Desktop and ProvisioningCheck, installs models into `[INSTALLDIR]\Models`, generates `[INSTALLDIR]\appsettings.user.json`, writes a deterministic model manifest, validates SHA-256 hashes, and blocks install success when provisioning fails.

Production builds require signed artifacts and secure config. Non-production unsigned/test-model builds are allowed only through explicit flags.

## Developer Bootstrap

`setup.ps1` is a non-destructive readiness script. It validates the local SDK and repository layout; it does not scaffold, overwrite, or mutate the solution.

```powershell
.\setup.ps1 -Restore -Build -Test -InstallerReadiness
```

Pinned SDK: see `dotnet_version.txt` and `global.json`.

## Validation

Core local gates:

```powershell
dotnet restore Poseidon.sln
dotnet build Poseidon.sln -c Release --no-restore
dotnet test tests/Poseidon.UnitTests/Poseidon.UnitTests.csproj -c Release --no-build
.\tests\scripts\Test-RepositoryHygiene.ps1
.\installer\build-installer.ps1 -ModelsPath ".artifacts\installer-models" -BuildProfile NonProduction -AllowTestModels -UnsignedDevelopmentBuild
.\installer\Validate-InstallerArtifacts.ps1 -BuildProfile NonProduction -AllowTestModels -AllowUnsigned
```

## Security Model

Production security is fail-closed:

- JWT secrets must be strong, protected, and referenced through secure secret storage.
- Management and agent API keys must be protected and non-placeholder.
- DPAPI secret references are used for production machine/user secrets.
- User config cannot weaken trusted machine model paths, hashes, strict mode, provider security mode, or security policy.
- Local model manifests require exact SHA-256 hashes.
- Installer artifacts and provisioning tools must be signed in production.

See `docs/security.md` and `docs/provisioning.md`.

## Documentation

- `docs/deployment.md`: enterprise install and operational deployment.
- `docs/installer.md`: WiX/Burn packaging contract.
- `docs/provisioning.md`: first-launch, machine config, model manifest, and recovery behavior.
- `docs/security.md`: authentication, DPAPI secret storage, signing, and management-plane security.
- `deploy/qa/Launch-Issue-Runbook.md`: launch diagnostics and support workflow.

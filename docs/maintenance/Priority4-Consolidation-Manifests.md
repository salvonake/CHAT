# Priority 4 Consolidation Manifests

## Deletion Manifest

Removed as generated or tracked artifact pollution:

- `publish/`
- `TestResults/`
- `.artifacts/`
- `installer/generated/`
- `installer/output/`
- `installer/obj/`
- `deploy/qa/evidence/`
- `build_log.txt`
- `build_diag.txt`
- `ingestion_build_diag.txt`
- `restore_diag.txt`
- `setup_log.txt`
- `test_detail.txt`
- `test_results.txt`
- `installer/models-placeholder/qwen2.5-14b.Q5_K_M.gguf`

Removed as unused legacy migration residue after active-reference verification:

- predecessor solution file
- predecessor source project tree
- predecessor test project tree
- predecessor WiX project files
- predecessor installer icon
- predecessor launch diagnostics collector
- `installer/Poseidon.Setup.iss`

## Retention Manifest

Retained as authoritative production assets:

- `Poseidon.sln`
- `src/Poseidon.*`
- `tests/Poseidon.*`
- `installer/Poseidon.Installer.wixproj`
- `installer/Poseidon.Bundle.wixproj`
- `installer/Package.wxs`
- `installer/Bundle.wxs`
- `installer/build-installer.ps1`
- `installer/Validate-InstallerArtifacts.ps1`
- `installer/prerequisites.json`
- `deploy/qa/Collect-PoseidonLaunchDiagnostics.ps1`
- `deploy/qa/Launch-Issue-Runbook.md`

## Rollback Manifest

Rollback is git-native:

- Restore deleted legacy files from the parent commit if a downstream consumer is discovered.
- Restore generated artifacts by rerunning the build, installer, or test command that produced them.
- Revert version pinning as a single change if a pinned package requires emergency replacement.
- Reintroduce a legacy path only with a new active-reference audit and explicit approval.

## Verification Scope

Deletion candidates were checked against:

- `Poseidon.sln`
- active `src/Poseidon.*` and `tests/Poseidon.*` project files
- official WiX/Burn project files and `.wxs` sources
- CI workflow
- deployment and test scripts
- active Poseidon documentation paths

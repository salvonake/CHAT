# Poseidon Upgrade, Repair, and Migration Runbook

## Upgrade Certification

Every production release must validate:

- previous supported Poseidon version to current version
- config preservation
- secret reference preservation
- model manifest replacement or preservation as intended
- provisioning-check execution after upgrade
- rollback on failed trust validation

## Repair Certification

Repair must validate:

- deleted model file restored or failure surfaced deterministically
- corrupted model file rejected by hash validation
- corrupted machine config rejected or repaired
- provisioning-check reruns
- first launch remains Full, Degraded, or Recovery deterministically

## Migration Certification

Legacy predecessor migration is not an active runtime path. If an enterprise customer requires migration from an older predecessor deployment, it must be certified as a project-specific migration package and not silently coupled to the standard Poseidon installer.

## Evidence

Attach logs for:

- install
- upgrade
- rollback
- repair
- provisioning-check
- first launch
- uninstall


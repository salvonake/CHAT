# Poseidon Production Support Governance

## Supported Deployment Modes

- Full local mode
- Degraded mode with intentional external LLM dependency
- External model mode when explicitly certified for the customer environment

## Release Cadence

- Production releases follow SemVer.
- Patch releases are allowed for security, installer, and reliability fixes.
- Emergency releases require the same signing and artifact validation gates as standard releases.

## Installer Servicing

Supported lifecycle operations:

- install
- repair
- upgrade
- rollback
- uninstall

Each operation must preserve deterministic startup behavior and produce diagnosable logs.

## Logging and Audit

Deployment support requests should collect:

- Windows Installer logs
- Burn logs
- provisioning-check logs
- startup diagnostics
- model manifest
- machine config with secrets redacted
- artifact checksum registry

## Escalation

Escalate to release engineering when:

- signature verification fails
- model hash validation fails
- machine config validation fails
- first launch enters unexpected state
- repair or rollback fails


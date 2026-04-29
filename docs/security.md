# Poseidon Security Model

Poseidon production security is fail-closed.

## Authentication

- JWT signing secrets require protected references and strong entropy.
- Issuer and audience are required outside explicit insecure Development mode.
- Primary and secondary JWT keys support rotation readiness.
- Placeholder and weak secrets are rejected.

## Secret Storage

Production config stores references such as `dpapi:LocalMachine:Poseidon/EncryptionPassphrase:v1`. Runtime resolves secret values through protected storage and logs only redacted state.

Plaintext secrets are allowed only in Development with explicit insecure development mode enabled.

## Management Plane

Management and agent credentials must be protected. Production management requests use signed timestamp/nonce headers. Auth failures, tamper attempts, invalid secret loads, and rotation events are logged without secret disclosure.

## Installer Trust

Production packaging requires:

- signed MSI, Burn bundle, and provisioning-check executable;
- pinned prerequisite hashes;
- model SHA-256 hashes in manifest and machine config;
- secure build provenance;
- no test models or placeholder values.

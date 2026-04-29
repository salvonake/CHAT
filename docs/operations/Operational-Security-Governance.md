# Poseidon Operational Security Governance

## Secret Governance

- Production secrets must not be stored as plaintext in release artifacts.
- Production config uses protected secret references.
- DPAPI deployment scope must match the deployment model.
- Secret injection is performed by deployment automation or authorized operations staff.

## Key Rotation

Rotation events must document:

- key type
- current key version
- next key version
- rotation window
- rollback plan
- validation result

JWT and management-plane keys support primary and secondary material to allow controlled rollover.

## Certificate Renewal

Certificate renewal must occur before expiry with:

- new certificate import
- thumbprint update
- test signing
- Authenticode verification
- release workflow dry run

## Incident Response

Security incidents must preserve:

- installer logs
- provisioning-check logs
- startup diagnostics
- management-plane auth failure logs
- release manifest and artifact checksums

Do not rotate or delete evidence before incident triage is complete.


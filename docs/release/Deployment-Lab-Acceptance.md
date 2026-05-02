# Poseidon Deployment Lab Acceptance Manifest

The Deployment Lab Acceptance Manifest is the canonical execution-proof artifact for Priority 8 production certification. It binds the production certification checklist to a specific lab environment, signed artifact set, deployment lifecycle run, runtime validation evidence, and release authority attestation.

This document defines how `deploy/enterprise/deployment-lab-acceptance.manifest.template.json` must be completed for a production release candidate.

## 1. Purpose

The manifest answers one question:

> Did this exact Poseidon release artifact pass the approved enterprise deployment lab under the approved certification contract?

It is not a design document and does not authorize engineering changes. It is an evidence envelope.

The manifest connects:

- The certification policy contract: `docs/release/Certification-Gate-Checklist.md`.
- The gate result object: `deploy/enterprise/certification-gate-checklist.template.json` or a populated equivalent.
- The release artifact identity: hashes, signatures, provenance, and version.
- The deployment lab identity: environment hash, account context, deployment channel, and host identity.
- The execution proof: install, repair, upgrade, rollback, uninstall, runtime startup, and fail-closed negative tests.
- The authority decision: final attestation.

## 2. Scope Boundary

The manifest is governance-only.

It must not be used to justify:

- runtime architecture changes,
- LLamaSharp changes,
- llama.cpp upgrades,
- provider abstraction changes,
- installer topology redesign,
- model-loader behavior changes,
- provisioning redesign,
- feature development.

Any engineering change discovered during lab execution requires a blocker record with reproducible failure evidence before remediation can be considered.

## 3. Manifest Layers

### 3.1 Identity Layer

The identity layer proves what was tested and where.

Required fields:

- `lab_id`
- `environment.name`
- `environment.environment_hash`
- `environment.deployment_context.channel`
- `build_identity.version`
- `build_identity.commit_hash` or `build_identity.provenance_binding_hash`
- `build_identity.artifact_hash`
- `build_identity.signature_chain_id`

The `environment_hash` should be derived from a stable lab profile: OS version, deployment tooling versions, policy baseline, installed prerequisites, and machine context.

### 3.2 Certification Binding Layer

The certification binding layer freezes the checklist and gate state used for the lab run.

Required fields:

- `certification_binding.checklist_path`
- `certification_binding.checklist_hash`
- `certification_binding.gate_manifest_path`
- `certification_binding.gate_manifest_hash`
- `certification_binding.gate_results`

The checklist hash prevents review drift. If `Certification-Gate-Checklist.md` changes, the lab manifest must be regenerated or explicitly tied to the previous checklist version.

### 3.3 Trust Chain Validation Layer

The trust chain validation layer records cryptographic and provisioning proof.

Production acceptance requires:

- `artifact_signature_valid = true`
- `timestamp_authority_valid = true`
- `hash_chain_valid = true`
- `installer_validation_passed = true`
- `provisioning_validation_passed = true`
- `tamper_detection_passed = true`

Each `true` value must have evidence references. Boolean assertions without evidence are not acceptable for release authority review.

### 3.4 Runtime Enforcement Proof Layer

The runtime enforcement proof layer records whether the installed release enforces production trust boundaries.

Production acceptance requires:

- `fail_closed_verified = true`
- `unsigned_artifact_rejected = true`
- `uncertified_model_blocked = true`
- `tokenizer_substitution_blocked = true`
- `plaintext_secret_detection_blocked = true`
- `manifest_report_mismatch_blocked = true`
- `native_backend_absence_blocked = true`

These checks prove invalid production states are blocked instead of degraded through.

### 3.5 Deployment Lifecycle Layer

The deployment lifecycle layer records enterprise deployment behavior.

Required lifecycle areas:

- interactive install,
- silent Burn install,
- silent MSI install where MSI-only deployment is approved,
- repair,
- upgrade,
- rollback,
- uninstall,
- SCCM,
- Intune,
- GPO,
- SYSTEM-context install,
- multi-user machine behavior.

Unsupported channels must be marked `NOT_APPLICABLE` with governance approval. They must not be omitted.

### 3.6 Runtime Verification Layer

The runtime verification layer records the installed application behavior after trust gates pass.

Required proof:

- startup mode,
- `CanAsk` state,
- error count,
- warning count,
- positive model/backend load signals,
- absence of forbidden signals.

Forbidden signals include:

- `FailClosedGuard` ambiguity,
- native backend load failure,
- `LoadWeightsFailedException`,
- MediatR commercial license warning,
- manifest integrity failure,
- provisioning failure.

### 3.7 Evidence Bundle Layer

The evidence bundle layer points to immutable logs, hashes, signatures, and trace IDs.

The bundle must be content-addressed by `bundle_sha256`. If any evidence file changes, the bundle hash must be regenerated.

### 3.8 Attestation Layer

The attestation layer is the release authority decision.

Default state is:

```json
"status": "BLOCKED"
```

Allowed final states:

- `PASS`
- `FAIL`
- `BLOCKED`

`PASS` is valid only when all required checklist gates are `PASS`, trust chain validations are true, runtime enforcement proofs are true, required lifecycle tests pass, and evidence is complete.

## 4. Acceptance Criteria

A deployment lab manifest is production-eligible only when all conditions are met:

1. `attestation.status == "PASS"`.
2. Every required certification gate is `PASS`.
3. Checklist hash matches the reviewed checklist.
4. Gate manifest hash matches the populated gate result object.
5. Artifact signature validation is true.
6. Timestamp authority validation is true.
7. Hash chain validation is true.
8. Installer validation is true.
9. Provisioning validation is true.
10. Tamper detection is true.
11. All runtime enforcement proofs are true.
12. Required deployment lifecycle tests are `PASS`.
13. Runtime verification has no forbidden signals.
14. Evidence bundle hash is present and reproducible.
15. Reviewer and release authority identity are recorded.

If any condition fails, production promotion remains blocked.

## 5. Review Workflow

1. Generate or populate the certification gate checklist manifest.
2. Compute the hash of `docs/release/Certification-Gate-Checklist.md`.
3. Compute the hash of the populated gate manifest.
4. Populate lab identity and environment hash.
5. Execute signed installer deployment in the lab.
6. Capture installer, provisioning, signature, runtime, and deployment lifecycle evidence.
7. Execute fail-closed negative tests.
8. Populate trust chain validation.
9. Populate runtime enforcement proofs.
10. Seal the evidence bundle and record `bundle_sha256`.
11. Review every gate result.
12. Complete attestation.

The release authority must reject manifests with missing evidence, stale hashes, ambiguous statuses, or approvals that predate final artifact mutation.

## 6. Relationship to Other Release Artifacts

The manifest does not replace:

- `Certification-Gate-Checklist.md`,
- `certification-gate-checklist.template.json`,
- `Release-Checklist.md`,
- `Release-Governance.md`,
- `Enterprise-Validation-Matrix.md`,
- signing evidence,
- compliance package,
- SBOM or dependency inventory.

It binds those artifacts into one deployment-lab execution record.

## 7. Current Priority 8 Interpretation

Until tokenizer production assets, signing infrastructure, protected MediatR secret deployment, and deployment lab execution are complete, the manifest must remain `BLOCKED`.

This is intentional. `BLOCKED` means production promotion has not yet been proven; it does not imply a runtime defect.


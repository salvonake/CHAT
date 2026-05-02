# Poseidon Final Certification Gate Checklist

This document is the authoritative pass/fail contract for promoting Poseidon from a release candidate to a production deployment artifact. It applies after software remediation and model certification Phase I are complete. It is intentionally narrow: it governs certification, evidence, and release authority. It does not authorize runtime redesign, provider changes, model-loader changes, installer topology changes, or speculative hardening.

## 1. Certification Control Rules

### 1.1 Status Vocabulary

Every gate must resolve to one of these statuses:

| Status | Meaning | Promotion impact |
| --- | --- | --- |
| `PASS` | Gate completed and evidence is attached. | May proceed. |
| `FAIL` | Gate executed and violated a production requirement. | Promotion blocked. |
| `BLOCKED` | Gate could not execute because an external prerequisite is absent. | Promotion blocked. |
| `NOT_APPLICABLE` | Gate does not apply to the selected deployment mode and the reason is documented. | May proceed only with release authority approval. |

No other status is accepted for release authority review.

### 1.2 Evidence Rules

Every gate must produce evidence with:

- UTC timestamp.
- Host identity.
- Release version.
- Git commit or provenance binding.
- Command or procedure executed.
- Exit code or equivalent result.
- Artifact hashes where files are involved.
- Signature details where signing is involved.
- A reviewer-readable summary.

Evidence must be immutable after gate completion. If a file changes after evidence is generated, the evidence must be regenerated.

### 1.3 Production Fail-Closed Rule

Production certification must fail closed when any of these are true:

- Artifact is unsigned or signature verification fails.
- Timestamp authority is absent or invalid.
- Production secret is plaintext, missing, unresolved, or leaked.
- Model artifact is uncertified, incompatible, or hash-mismatched.
- Tokenizer policy is unsatisfied.
- Manifest and certification report diverge.
- ProvisioningCheck fails.
- SecurityConfigurationValidator fails.
- Runtime starts only because a security-critical validation was bypassed.

Product `Degraded` mode may be valid only when it is an intended operating mode and not a bypass of signing, secrets, provisioning, model certification, or tokenizer requirements.

## 2. Gate Summary

| Gate ID | Gate | Required status for production |
| --- | --- | --- |
| `GATE-ARTIFACT-INTEGRITY` | Artifact integrity and reproducibility | `PASS` |
| `GATE-SIGNING` | Authenticode signing and timestamping | `PASS` |
| `GATE-TOKENIZER` | Tokenizer certification | `PASS` |
| `GATE-MODEL` | Model certification | `PASS` |
| `GATE-SECRETS` | Protected secret boundary | `PASS` |
| `GATE-PROVISIONING` | Provisioning and config validation | `PASS` |
| `GATE-DEPLOYMENT` | Enterprise deployment trust chain | `PASS` |
| `GATE-FAIL-CLOSED` | Negative fail-closed tests | `PASS` |
| `GATE-RUNTIME` | Runtime startup validation | `PASS` |
| `GATE-AUDIT` | Evidence and compliance package | `PASS` |
| `GATE-RELEASE-AUTHORITY` | Governance approval | `PASS` |

## 3. Gate Definitions

### 3.1 `GATE-ARTIFACT-INTEGRITY`

Purpose: Prove the production artifact set is complete, deterministic, and traceable.

Pass criteria:

- Release version is declared consistently across MSI, Burn bundle, release package, manifests, and evidence.
- Build input is tied to a clean Git commit or an approved provenance binding with patch hash.
- `dotnet restore Poseidon.sln --locked-mode` passes.
- `dotnet build Poseidon.sln -c Release --no-restore` passes.
- Unit tests pass.
- Installer artifacts exist:
  - `Poseidon.Installer.msi`
  - `Poseidon.Bundle.exe`
  - `Setup.exe`
  - `model-manifest.json`
  - `model-certification-report.json` when full local LLM mode is used
  - `build-provenance.json`
  - `prerequisite-validation.json`
  - `native-backend-validation.json`
  - `signing-report.json`
- `SHA256SUMS.txt` or equivalent checksum registry is generated after final artifact mutation.
- Checksums are reproducible between build output, release package, and deployment lab copy.

Fail conditions:

- Missing artifact.
- Empty artifact.
- Hash mismatch.
- Dirty or unbound source state.
- Generated evidence predates artifact mutation.
- Lock-file drift.

Required evidence:

- Build evidence.
- Test evidence.
- Artifact inventory.
- Hash registry.
- Provenance binding.

### 3.2 `GATE-SIGNING`

Purpose: Prove all production-delivered executables and installer artifacts are trusted by the enterprise signing policy.

Pass criteria:

- `signtool.exe` is installed on the release host.
- Approved certificate is available by thumbprint or approved secure provider.
- Certificate is not expired or revoked.
- RFC3161 timestamp authority is configured and reachable.
- These artifacts are signed:
  - `Poseidon.Installer.msi`
  - `Poseidon.Bundle.exe`
  - `Setup.exe`
  - `provisioning-check.exe`
  - `ModelPayloadInstaller.exe` when external LLM payload mode is used
  - any other exposed executable or service binary included in the release package
- `Get-AuthenticodeSignature` reports `Valid` for all signed artifacts.
- Signing report contains signer subject, certificate thumbprint, timestamp URL, and signature status.
- Production build fails if signing is unavailable.

Fail conditions:

- Missing `signtool.exe`.
- Missing or inaccessible certificate.
- Invalid, expired, or revoked certificate.
- Timestamp failure.
- Any unsigned production artifact.
- Any signature status other than `Valid`.

Required evidence:

- Signing preflight.
- Signing report.
- Authenticode verification output.
- Certificate metadata.
- Post-sign hashes.

### 3.3 `GATE-TOKENIZER`

Purpose: Prove tokenizer assets are deterministic, versioned, and bound to the certified model release.

Pass criteria:

- Production tokenizer policy is `required`.
- Required tokenizer assets are declared and present.
- `vocab.txt` or an approved equivalent is versioned as a release artifact.
- Tokenizer asset SHA-256 is recorded.
- Tokenizer identity is bound to the model certification evidence.
- No runtime tokenizer substitution path is used in Production.
- No Production warning override is used.
- Encoding/decoding validation suite passes when available for the selected tokenizer.

Fail conditions:

- Missing tokenizer asset.
- Tokenizer hash mismatch.
- Tokenizer policy is `warning` in Production.
- `warningAccepted=true` in Production.
- Runtime can silently substitute a different tokenizer.
- Tokenizer evidence does not identify the model it belongs to.

Required evidence:

- Tokenizer asset inventory.
- Tokenizer checksum.
- Certification report tokenizer section.
- Encoding/decoding validation output or documented approved waiver.

### 3.4 `GATE-MODEL`

Purpose: Prove the model artifact is certified for Poseidon’s approved runtime boundary before runtime trust is granted.

Pass criteria:

- Model SHA-256 and size are recorded.
- GGUF magic bytes and version are validated.
- Architecture is certified for the selected backend.
- Quantization is certified for the selected backend.
- Tensor metadata is structurally valid.
- Certification report targets `LLamaSharp 0.19.0 CPU AVX2` unless a future approved matrix supersedes it.
- Manifest schema is v3 or later.
- Manifest LLM entry contains certification fields:
  - `architecture`
  - `quantization`
  - `ggufVersion`
  - `certifiedBackend`
  - `certifiedAtUtc`
  - `compatibilityStatus`
  - `tokenizerPolicy`
  - `warningAccepted`
  - `certificationReportHash`
- Manifest/report parity passes.
- ProvisioningCheck validates model file, manifest, certification report, and report hash.
- No uncertified model execution path exists in Production.

Fail conditions:

- Unknown or blocked architecture.
- Unsupported quantization.
- GGUF parse failure.
- Manifest/report mismatch.
- Certification report hash mismatch.
- Production model status is not compatible.
- Model file hash differs from manifest or report.

Required evidence:

- `model-certification-report.json`.
- `model-manifest.json`.
- Model file hash.
- ProvisioningCheck output.
- Compatibility matrix decision.

### 3.5 `GATE-SECRETS`

Purpose: Prove production secrets are protected and not persisted or exposed as plaintext.

Pass criteria:

- MediatR commercial license is supplied through protected secret reference.
- Encryption passphrase is supplied through protected secret reference.
- Production machine config contains secret references, not plaintext values.
- Secret references resolve on the deployment host.
- Diagnostics distinguish missing, corrupt, and invalid secrets without logging secret values.
- Logs, configs, installer artifacts, evidence files, and DI graphs do not expose secret material.

Fail conditions:

- Plaintext production secret in config, logs, evidence, or installer artifact.
- Missing secret reference.
- Unresolved secret reference.
- Placeholder secret.
- Weak or low-entropy secret.
- Production startup succeeds without required secret.

Required evidence:

- Secret preflight report.
- Protected secret reference validation.
- Redacted configuration snapshot.
- Log scan showing no secret value leakage.

### 3.6 `GATE-PROVISIONING`

Purpose: Prove the installed machine configuration and model assets satisfy the production trust contract before services run.

Pass criteria:

- `provisioning-check.exe` is signed.
- Staged build provisioning validation passes.
- Post-install provisioning validation passes.
- Machine config is valid JSON.
- Machine config declares the expected deployment mode and build profile.
- Retrieval strict mode is enabled.
- Model hashes match config, manifest, report, and installed files.
- Native backend validation report passes.
- Production config validation passes.

Fail conditions:

- ProvisioningCheck nonzero exit code.
- Config JSON invalid.
- Mode mismatch.
- Hash mismatch.
- Native backend payload missing.
- SecurityConfigurationValidator failure.

Required evidence:

- Build-time provisioning log.
- Post-install provisioning log.
- Machine config snapshot with secrets redacted.
- Native backend validation report.

### 3.7 `GATE-DEPLOYMENT`

Purpose: Prove the release can be installed and serviced through supported enterprise deployment mechanisms.

Pass criteria:

- Burn silent install succeeds.
- MSI silent install succeeds where MSI-only deployment is approved.
- Repair succeeds.
- Uninstall succeeds.
- Upgrade from previous supported release succeeds.
- Rollback behavior is documented and validated.
- SYSTEM-context install succeeds.
- Multi-user machine behavior is validated.
- SCCM, Intune, or GPO deployment evidence is attached for each supported channel.
- Installed version matches release version.
- Installed hashes match release package hashes, excluding approved machine-generated files.

Fail conditions:

- Install failure.
- Repair failure.
- Uninstall failure.
- Upgrade failure.
- Stale cached payload reuse.
- Installed file hash mismatch.
- Unsupported deployment channel claimed without evidence.

Required evidence:

- Burn logs.
- MSI logs.
- Registry version proof.
- Installed file inventory and hashes.
- Deployment channel reports.

### 3.8 `GATE-FAIL-CLOSED`

Purpose: Prove invalid production trust states are blocked instead of tolerated.

Pass criteria:

- Unsigned production artifact is rejected.
- Missing tokenizer is rejected.
- Missing certification report is rejected.
- Certification report hash mismatch is rejected.
- Model hash mismatch is rejected.
- Plaintext production MediatR license is rejected.
- Missing protected MediatR secret is rejected.
- Invalid production config is rejected.
- Native backend absence is rejected.

Fail conditions:

- Any injected invalid state permits production promotion.
- Runtime starts because a security-critical validation degraded or warned through.
- Installer succeeds despite certification violation.

Required evidence:

- Negative test matrix.
- Injected failure logs.
- Exit codes.
- Blocker classification records.

### 3.9 `GATE-RUNTIME`

Purpose: Prove the installed release reaches an approved runtime state after all pre-runtime gates pass.

Pass criteria:

- Startup logs are captured from the installed release.
- No `FailClosedGuard` ambiguity.
- No native backend load failure.
- No provisioning failure.
- No manifest integrity failure.
- No model certification failure.
- No MediatR license warning in Production.
- LLM load success is present for full local mode.
- Runtime mode is `Full`, or `Degraded` only when explicitly approved and not caused by a security gate bypass.
- `Errors=0` for approved release startup.

Fail conditions:

- Startup exception.
- Security warning accepted in Production.
- Model load failure for the certified production model.
- Silent fallback around required security, model, tokenizer, or secret validation.

Required evidence:

- `startup.log`.
- `app.log`.
- Runtime mode summary.
- Positive model/backend load signal.

### 3.10 `GATE-AUDIT`

Purpose: Prove release evidence is complete, immutable, and reviewer-ready.

Pass criteria:

- Release package contains:
  - `INDEX.txt`
  - `STATUS.txt`
  - `SHA256SUMS.txt`
  - build evidence
  - signing evidence
  - deployment evidence
  - runtime evidence
  - model certification report
  - manifest
  - provenance
  - compliance package
  - SBOM or dependency inventory
- Evidence files are hashed after final mutation.
- Release status accurately distinguishes certification package, release candidate, and production-approved release.
- Every gate status is represented in the control-plane manifest.

Fail conditions:

- Missing evidence.
- Stale evidence.
- Hash mismatch.
- Ambiguous release status.
- Evidence stored only in temporary local paths.

Required evidence:

- Final release package.
- Integrity pass report.
- Control-plane manifest.

### 3.11 `GATE-RELEASE-AUTHORITY`

Purpose: Prove accountable owners approved the release for production deployment.

Pass criteria:

- Release owner approval is recorded.
- Security owner approval is recorded.
- Deployment owner approval is recorded.
- Governance authority approval is recorded.
- Any `NOT_APPLICABLE` gate has explicit justification.
- Remaining risks are classified and accepted by the proper authority.

Fail conditions:

- Missing approval.
- Approval predates final artifact mutation.
- Approval references a different artifact hash or version.
- Open `FAIL` or `BLOCKED` gate.

Required evidence:

- Signed approval record or equivalent enterprise governance artifact.
- Final gate manifest.
- Final release checksum registry.

## 4. Minimum Production Gate Commands

The release authority may add environment-specific commands, but these are the minimum repository-level commands:

```powershell
dotnet restore Poseidon.sln --locked-mode
dotnet build Poseidon.sln -c Release --no-restore
dotnet test tests/Poseidon.UnitTests/Poseidon.UnitTests.csproj -c Release --no-build
.\tests\scripts\Test-RepositoryHygiene.ps1
.\tests\scripts\Test-ReleaseSigningReadiness.ps1 -BuildProfile Production -SigningCertificateThumbprint "<thumbprint>"
.\installer\build-installer.ps1 -Configuration Release -BuildProfile Production -SigningCertificateThumbprint "<thumbprint>" -ModelsPath .\installer\models
.\installer\Validate-InstallerArtifacts.ps1 -BuildProfile Production -InstallerMode full
.\tests\scripts\Test-EnterpriseDeploymentEvidence.ps1 -RequireCertified
.\tests\scripts\Test-FinalEnterpriseCertification.ps1 -RequireCertified
```

Production promotion must stop at the first failure. The blocker must be recorded using the blocker schema before remediation is considered.

## 5. Current Priority 8 Known Gate Status

As of the Phase I model certification baseline:

| Gate | Current status | Reason |
| --- | --- | --- |
| Artifact integrity | `PASS` for NonProduction certification package | Production package pending signing/tokenizer completion. |
| Signing | `BLOCKED` | `signtool.exe`, certificate, and timestamp authority are external prerequisites. |
| Tokenizer | `BLOCKED` for Production | `vocab.txt` or approved equivalent is required for Production. |
| Model | `PASS` for TinyLlama validated payload; `FAIL/BLOCKED` for original Qwen target | Current certified payload is LLaMA/Q4_0 under preserved filename contract. Qwen remains unsupported by the current compatibility matrix. |
| Secrets | `BLOCKED` for Production | Protected MediatR commercial license secret must be provisioned on the deployment host. |
| Provisioning | `PASS` for NonProduction with explicit tokenizer warning | Production requires required tokenizer and protected secrets. |
| Deployment | `BLOCKED` | Enterprise lab evidence pending. |
| Fail-closed | `PASS` for implemented software gates | External gates still need production lab proof. |
| Runtime | `PASS` for validated TinyLlama payload | Production evidence must be refreshed after signing/tokenizer/secret completion. |
| Audit | `PARTIAL` | Certification package exists; production package pending. |
| Release authority | `BLOCKED` | Requires all previous gates to pass. |


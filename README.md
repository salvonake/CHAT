# LegalAI — Evidence-Constrained Legal Knowledge Engine

A sovereign, offline-first, cryptographically secured legal AI system that assists lawyers and judges in analyzing confidential legal documents with **zero hallucination tolerance**.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    React Frontend (RTL)                  │
├─────────────────────────────────────────────────────────┤
│                  ASP.NET Core Web API                   │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌────────┐ │
│  │  /ask     │  │ /docs    │  │ /metrics │  │ /audit │ │
│  └──────────┘  └──────────┘  └──────────┘  └────────┘ │
├─────────────────────────────────────────────────────────┤
│               Application Layer (MediatR CQRS)          │
│  ┌───────────────────────────────────────────────────┐  │
│  │  AskLegalQuestion → EC-RAG Pipeline → LegalAnswer │  │
│  │  IngestDocument → PDF → Chunks → Embed → Store    │  │
│  └───────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────┤
│  ┌─────────────┐  ┌────────────┐  ┌───────────────┐   │
│  │  Retrieval   │  │  Ingestion │  │   Security    │   │
│  │  Pipeline    │  │  Pipeline  │  │   Layer       │   │
│  │             │  │            │  │               │   │
│  │ QueryAnalysis│  │ PdfPig     │  │ AES-256-GCM  │   │
│  │ HybridSearch│  │ ArabicNorm │  │ HKDF Keys    │   │
│  │ BM25+Vector │  │ LegalChunk │  │ HMAC Audit   │   │
│  │ Reranking   │  │ Embedding  │  │ InjectionDet │   │
│  │ ContextBudget│ │            │  │              │   │
│  └─────────────┘  └────────────┘  └───────────────┘   │
├─────────────────────────────────────────────────────────┤
│                 Infrastructure Layer                    │
│  ┌──────────┐  ┌──────────┐  ┌──────┐  ┌───────────┐  │
│  │  Qdrant  │  │  Ollama  │  │SQLite│  │ Telemetry │  │
│  │ (vectors)│  │  (LLM)   │  │(docs)│  │ (metrics) │  │
│  └──────────┘  └──────────┘  └──────┘  └───────────┘  │
└─────────────────────────────────────────────────────────┘
```

## Prerequisites

1. **.NET 8 SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
2. **Docker** — For Qdrant vector database
3. **Ollama** — [Download](https://ollama.com/) for local LLM inference

## Quick Start

### 1. Install Dependencies

```powershell
# Install .NET 8 SDK (winget)
winget install Microsoft.DotNet.SDK.8

# Install Ollama
winget install Ollama.Ollama

# Install Docker Desktop (for Qdrant)
winget install Docker.DockerDesktop
```

### 2. Pull Required Models

```powershell
# Pull the LLM model (Arabic-capable)
ollama pull qwen2.5:14b

# Pull the embedding model
ollama pull nomic-embed-text
```

### 3. Start Qdrant

```powershell
cd deploy/docker
docker compose up -d
```

### 4. Build & Run

```powershell
# Run the setup script (creates solution, restores packages)
.\setup.ps1

# Build the solution
dotnet build

# Run the API server
dotnet run --project src/LegalAI.Api

# In another terminal, run the Worker Service (file watcher)
dotnet run --project src/LegalAI.WorkerService
```

## Build Setup.exe (Professional Installer)

Use the WiX-based installer pipeline in `installer/` to produce a full `Setup.exe` that chains prerequisites and installs LegalAI.

What `Setup.exe` installs:
- LegalAI Desktop MSI payload
- Microsoft VC++ Redistributable (x64)
- .NET 8 Windows Desktop Runtime (x64)

Ollama runtime is not bundled by default; install it separately if you use Ollama-backed inference.
On first run, the setup wizard includes an optional Ollama onboarding panel with connection test and download shortcut.
AI model files are not bundled in Setup by default; during first-run setup you can select already-installed GGUF/ONNX paths.

Build command:

```powershell
./installer/build-installer.ps1 -Configuration Release -ModelsPath "C:\path\to\models"
```

If any model file is too large for MSI embedding (>= 2 GB), the build now automatically switches to external-model mode, still produces the installer, and writes a deployment manifest with SHA-256 hashes.

Dev build without large model payloads:

```powershell
./installer/build-installer.ps1 -Configuration Release -SkipModels
```

If prerequisites are already staged in `installer/prereqs`, skip re-downloading:

```powershell
./installer/build-installer.ps1 -Configuration Release -SkipPrereqDownload
```

Output artifacts:
- `installer/output/LegalAI-Setup-YYYYMMDD.msi`
- `installer/output/LegalAI-Setup-YYYYMMDD.exe`
- `installer/output/external-models.manifest.json` (generated when model files are validated)

### 5. Ingest Documents

```powershell
# Place PDF files in the 'pdfs' directory (auto-indexed by worker service)
# Or manually via API:

# Ingest a single document
curl -X POST http://localhost:5000/api/documents/ingest `
  -H "Content-Type: application/json" `
  -d '{"filePath": "C:\\path\\to\\document.pdf"}'

# Ingest entire directory
curl -X POST http://localhost:5000/api/documents/ingest/directory `
  -H "Content-Type: application/json" `
  -d '{"directoryPath": "C:\\path\\to\\pdfs"}'
```

### 6. Ask Questions

```powershell
curl -X POST http://localhost:5000/api/ask `
  -H "Content-Type: application/json" `
  -d '{"question": "ما هي السوابق القضائية المتعلقة بالمادة 145؟"}'
```

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/ask` | POST | Ask a legal question (EC-RAG pipeline) |
| `/api/documents` | GET | List indexed documents |
| `/api/documents/ingest` | POST | Ingest a single PDF |
| `/api/documents/ingest/directory` | POST | Ingest a directory of PDFs |
| `/api/documents/quarantine` | GET | View quarantined documents |
| `/api/documents/stats` | GET | Index statistics |
| `/api/health` | GET | System health check |
| `/api/metrics` | GET | System metrics |
| `/api/ops/overview` | GET | Operations overview |
| `/api/audit` | GET | Audit log entries |
| `/api/audit/verify` | GET | Verify audit chain integrity |

## Configuration

Edit `src/LegalAI.Api/appsettings.json`:

```json
{
  "Qdrant": { "Host": "localhost", "Port": 6334 },
  "Ollama": { "Url": "http://localhost:11434", "Model": "qwen2.5:14b" },
  "Embedding": { "Provider": "ollama", "Model": "nomic-embed-text", "Dimension": 768 },
  "Security": { "EncryptionEnabled": true, "EncryptionPassphrase": "YOUR_STRONG_PASSPHRASE" },
  "Retrieval": { "StrictMode": true, "TopK": 10, "SimilarityThreshold": 0.45 }
}
```

## Security

- **AES-256-GCM** encryption for vector store and metadata
- **Argon2id** key derivation from passphrase
- **HKDF** key hierarchy (vector key, audit key, session key)
- **HMAC-signed** append-only audit chain
- **Prompt injection detection** (English + Arabic patterns)
- **Case namespace isolation** — prevent cross-case data leakage

## Evidence-Constrained Guarantees

- **Never** answers outside indexed corpus
- **Every** claim must have a citation
- **Abstains** when evidence is insufficient
- **Dual-pass** self-validation in strict mode
- **Confidence scoring** on every response
- **No** training data recall or external knowledge

## Project Structure

```
src/
├── LegalAI.Domain/           # Entities, interfaces, value objects
├── LegalAI.Application/      # CQRS commands/queries (MediatR)
├── LegalAI.Ingestion/        # PDF extraction, Arabic chunking, embedding
├── LegalAI.Retrieval/        # Multi-stage retrieval pipeline, BM25
├── LegalAI.Security/         # AES-GCM, Argon2, HKDF, injection detection
├── LegalAI.Infrastructure/   # Qdrant, Ollama, SQLite, telemetry
├── LegalAI.Api/              # ASP.NET Core Web API
└── LegalAI.WorkerService/    # Background indexing Windows Service
```

## CI Pipeline

GitHub Actions workflow is defined in [.github/workflows/ci.yml](.github/workflows/ci.yml).

- Unit tests run on Windows in every push/PR.
- Unit tests collect Cobertura coverage (`coverage.cobertura.xml`), upload it as `unit-coverage-cobertura`, and enforce a minimum line coverage threshold (`COVERAGE_MIN`, default `30`).
- Optional: set repository variable `LEGALAI_UNIT_COVERAGE_MIN` to override the CI unit coverage threshold without editing workflow YAML.
- Optional strict policy toggle: set `LEGALAI_CI_STRICT_COVERAGE=true` to enforce strict threshold mode in CI.
- Optional strict threshold value: set `LEGALAI_CI_STRICT_COVERAGE_MIN` (default `70`) for strict policy mode.
- Effective CI threshold resolution:
  - `LEGALAI_CI_STRICT_COVERAGE=true` -> use `LEGALAI_CI_STRICT_COVERAGE_MIN`.
  - otherwise -> use `LEGALAI_UNIT_COVERAGE_MIN` (or default `30`).
- Local scripts can also read `LEGALAI_UNIT_COVERAGE_MIN` when `-CoverageMin` is not explicitly passed.
- Qdrant integration tests run on Ubuntu with a Qdrant service container.
- ONNX integration tests run only when repository secret `LEGALAI_ONNX_MODEL_URL` is configured.
- Readiness Gate aggregates CI outcomes into one status check (`success` requires unit + Qdrant, and ONNX when enabled).
- Readiness Gate summary includes per-job results plus an explicit overall `PASSED`/`FAILED` row.
- CI jobs clean `TestResults` before each test run to keep TRX/coverage artifacts deterministic.
- CI uses workflow concurrency (`cancel-in-progress`) to auto-cancel superseded runs on the same branch/PR.
- CI jobs have timeout limits to prevent hung runs from blocking merge readiness.
- CI enables NuGet package caching in `actions/setup-dotnet` to speed up repeated restore/build/test runs.

Recommended branch protection setting: require the `Readiness Gate` status check for `main`/`master`.

### Integration Test Environment Variables

- `LEGALAI_RUN_INTEGRATION=true` enables integration test execution.
- `LEGALAI_QDRANT_HOST` and `LEGALAI_QDRANT_PORT` select Qdrant endpoint (defaults to `127.0.0.1:6334`).
- `LEGALAI_ONNX_MODEL_PATH` must point to a valid `.onnx` file for ONNX integration tests.

### Local Integration Run

Preferred (one command):

```powershell
./tests/scripts/Run-IntegrationTests.ps1 -StartQdrant -OnnxModelPath "C:\path\to\model.onnx" -Release
```

Fast rerun without rebuilding:

```powershell
./tests/scripts/Run-IntegrationTests.ps1 -Release -NoBuild
```

`-NoBuild` requires existing compiled test binaries; run once without `-NoBuild` after clean changes.

Manual setup:

```powershell
$env:LEGALAI_RUN_INTEGRATION = "true"
$env:LEGALAI_QDRANT_HOST = "127.0.0.1"
$env:LEGALAI_QDRANT_PORT = "6334"
$env:LEGALAI_ONNX_MODEL_PATH = "C:\path\to\model.onnx"

docker compose -f deploy/docker/docker-compose.yml up -d
dotnet test tests/LegalAI.IntegrationTests/LegalAI.IntegrationTests.csproj
```

### Local Unit Test Run

All local test runner scripts require a working `dotnet` SDK in `PATH` (for this repo, SDK 8.x).
They also clear stale local result files before each run so summaries and gates use only current execution outputs.
Common helper logic for local test runners lives in `tests/scripts/Common.ps1`.
If `dotnet` is missing from the current terminal `PATH`, scripts automatically try `%LOCALAPPDATA%\\Microsoft\\dotnet` before failing.
Integration runner restores modified `LEGALAI_*` process environment variables when it finishes.
Input validation: `CoverageMin` accepts `0..100`; `QdrantPort` accepts `1..65535`.

Preferred (one command):

```powershell
./tests/scripts/Run-UnitTests.ps1 -Release
```

Optional filtering example:

```powershell
./tests/scripts/Run-UnitTests.ps1 -Release -Filter "FullyQualifiedName~FileWatcherServiceTests"
```

When filtering with `-Filter`, coverage is still collected but the coverage gate is skipped by default.
Use `-EnforceCoverageOnFilteredRun` to enforce the threshold for filtered runs.

Optional coverage gate example:

```powershell
./tests/scripts/Run-UnitTests.ps1 -Release -CollectCoverage -CoverageMin 30
```

Fast rerun without rebuilding:

```powershell
./tests/scripts/Run-UnitTests.ps1 -Release -NoBuild
```

### Combined Local Test Run

Run unit tests first, then integration tests, with a consolidated summary:

```powershell
./tests/scripts/Run-AllTests.ps1 -Release -IncludeIntegration
```

With optional Qdrant startup and ONNX model path:

```powershell
./tests/scripts/Run-AllTests.ps1 -Release -IncludeIntegration -StartQdrant -OnnxModelPath "C:\path\to\model.onnx"
```

With optional unit coverage gate during combined runs:

```powershell
./tests/scripts/Run-AllTests.ps1 -Release -IncludeIntegration -CollectUnitCoverage -CoverageMin 30
```

Stricter combined gate (recommended for pre-merge/release candidate checks):

```powershell
./tests/scripts/Run-AllTests.ps1 -Release -IncludeIntegration -CollectUnitCoverage -CoverageMin 70
```

Fast rerun without rebuilding:

```powershell
./tests/scripts/Run-AllTests.ps1 -Release -IncludeIntegration -CollectUnitCoverage -NoBuild
```

Fast strict combined rerun without rebuilding:

```powershell
./tests/scripts/Run-AllTests.ps1 -Release -IncludeIntegration -CollectUnitCoverage -CoverageMin 70 -NoBuild
```

Recommended readiness command before release:

```powershell
./tests/scripts/Run-Readiness.ps1 -Release -CoverageMin 30
```

Stricter readiness gate (recommended for pre-merge/release candidate checks):

```powershell
./tests/scripts/Run-Readiness.ps1 -Release -CoverageMin 70
```

Fast readiness rerun without rebuilding:

```powershell
./tests/scripts/Run-Readiness.ps1 -Release -NoBuild -CoverageMin 30
```

Fast strict readiness rerun without rebuilding:

```powershell
./tests/scripts/Run-Readiness.ps1 -Release -NoBuild -CoverageMin 70
```

Optional readiness run with local Qdrant startup:

```powershell
./tests/scripts/Run-Readiness.ps1 -Release -StartQdrant -CoverageMin 30
```

When `-CollectUnitCoverage` is enabled, the combined summary also prints unit coverage percentage, minimum threshold, and gate status.
If `-UnitFilter` is used, the coverage gate is skipped by default; pass `-EnforceCoverageOnFilteredRun` to enforce it.

### VS Code One-Click Tasks

Use `Tasks: Run Task` in VS Code. Added tasks in [.vscode/tasks.json](.vscode/tasks.json):

- `Tests: Unit (Release)`
- `Tests: Unit (NoBuild)`
- `Tests: Unit (FileWatcher only)`
- `Tests: Unit (Qdrant only)`
- `Tests: Unit + Coverage Gate`
- `Tests: Unit + Coverage Gate (NoBuild)`
- `Tests: Integration`
- `Tests: Integration (Start Qdrant)`
- `Tests: Integration (NoBuild)`
- `Tests: Integration (NoBuild + Start Qdrant)`
- `Tests: All (Release)`
- `Tests: All + Integration (Release)`
- `Tests: All + Integration (NoBuild)`
- `Tests: All + Integration + Coverage Gate`
- `Tests: All + Integration + Coverage Gate (NoBuild)`
- `Tests: All + Integration + Coverage Gate (Strict 70%)`
- `Tests: All + Integration + Coverage Gate (Strict 70% NoBuild)`
- `Tests: Readiness (Release)`
- `Tests: Readiness (Strict 70%)`
- `Tests: Readiness (NoBuild)`
- `Tests: Readiness (Strict 70% NoBuild)`
- `Tests: Readiness (Start Qdrant)`
- `Tests: Readiness (NoBuild + Start Qdrant)`

## Legal Disclaimer

This system provides evidence-grounded analysis based solely on indexed documents. It does **not** replace judicial reasoning, provide verdicts, or issue sentencing recommendations. All outputs must be reviewed by qualified legal professionals.

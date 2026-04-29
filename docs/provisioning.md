# Provisioning and Runtime State

Provisioning is deterministic and happens before normal hosted services consume runtime configuration.

## Startup Flow

1. Initialize configuration and logging.
2. Evaluate machine/user provisioning state.
3. Show setup wizard when required.
4. Persist valid user config atomically.
5. Reload configuration and runtime bindings.
6. Validate model integrity and provider readiness.
7. Enter Full, Degraded, or Recovery.
8. Start hosted services only after provisioning success or Recovery is established.

## Config Precedence

Trusted machine config controls model hashes, strict mode, security policies, trusted model paths, and provider security mode. User config can store UI preferences, watch folders, diagnostics settings, and user-local UX values only.

## Model Path Resolution

1. Explicit trusted config path.
2. `%LOCALAPPDATA%\Poseidon\Models`.
3. Installed application `Models` directory.

Missing embedding, invalid Ollama setup, invalid hash, or missing required LLM causes Recovery unless the deployment explicitly declares a valid Degraded mode.

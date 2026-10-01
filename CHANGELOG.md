# Changelog

## 2.4.0 - 2026-10-01

### Repository identity and protocol
- Completed the LLM Continuity Toolkit source-tree identity.
- Renamed application, core, regression, architecture-validation, parity-audit, solution, and workflow paths to current product-neutral names.
- Versioned Continuation Markdown framing and bundle/fingerprint identifiers under the current continuity protocol.
- Added a repository sanitation gate that rejects the prohibited legacy identity sequence in tracked paths or file contents.

### Privacy and security
- Retained local-only processing, zero application telemetry, no account/API integration, no persistent conversation cache/database, and no background service/updater/startup component.
- Retained pseudonymized redacted diagnostics and fail-safe redaction behavior for partially failed imports.
- Retained direct JSON and ZIP input limits, hostile-input checks, raw-record integrity verification, controlled-root zero-residue auditing, and zero application-owned network endpoints while idle.

### Build and release engineering
- Preserved immutable GitHub Action SHAs, deterministic SDK selection, reproducible portable builds, PE mitigation checks, startup survival, and tag/source version parity.
- Replaced version-specific validation naming with purpose-based build, architecture-validation, and release terminology.
- Current release packaging remains a self-contained Windows x64 portable application with required Microsoft/.NET legal notices.

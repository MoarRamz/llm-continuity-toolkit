# Security

## Trust model

LLM Continuity Toolkit processes official OpenAI account export data locally. Export data is treated as untrusted structured input.

The application is designed to operate without application network requests, application telemetry/analytics, API credentials, administrator privileges, services, startup entries, background processes, or a persistent conversation database/cache.

## Input hardening

Validation covers direct JSON and ZIP size ceilings, duplicate archive entries, zero-length or malformed input, suspicious compression ratios, excessive graph/mapping structures, oversized visible messages, broken/cyclic active paths, duplicate stable conversation IDs, unknown future visible content, and raw-source mutation after indexing.

Readable exports fail closed when active visible structured content is unsupported. Complete Conversation JSON remains available when the raw record can be preserved without interpreting unsupported content.

## Output integrity

Continuation Markdown is structurally verified before finalization. Multi-conversation bundles are staged, packaged, reopened, checked for safe flat entry names, validated against the embedded manifest/instructions contract, and re-hashed before acceptance.

Atomic/staged output is cleaned up on cancellation or failure.

## Diagnostic privacy

Activity Log data is memory-only by default. Full log saving is explicit. **Save Redacted...** pseudonymizes registered conversation titles, stable conversation identifiers, and local filesystem values, including candidates retained across failed imports.

Real exports, transcripts, generated archives, and private diagnostics must never be committed.

## Runtime and supply-chain controls

CI uses an exact SDK pin and immutable action SHAs with checkout credentials disabled. Security validation includes source-policy checks, repository identity sanitation, reproducible publishing, ASLR/DEP/high-entropy VA verification, startup survival, zero application-owned TCP/UDP endpoints while idle, and controlled-root zero-residue auditing.

The Windows application manifest must remain `asInvoker`.

## Release verification

Stable packages are built only from an explicit immutable semantic-version tag whose version matches `Directory.Build.props`. Release output includes per-file SHA-256 hashes, package SHA-256, provenance, the project license, and applicable Microsoft/.NET notice files.

The project is not currently Authenticode-signed with a publicly trusted code-signing certificate. Windows SmartScreen may therefore show an unknown-publisher or reputation warning for a fresh download. Do not disable SmartScreen globally; verify the published package hash before running the application.

## Reporting

Do not include real conversation exports, private logs, access tokens, personal filesystem paths, or other sensitive data in a public issue. Reproduce security findings with synthetic data whenever possible.

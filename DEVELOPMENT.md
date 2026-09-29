# Development Notes

## Design constraints

LLM Continuity Toolkit is intentionally a small, portable, local-first Windows utility for official OpenAI account data exports.

Do not introduce persistent infrastructure without a compelling functional requirement. In particular, avoid conversation caches, databases, application telemetry, cloud synchronization, background services, automatic updaters, account/API integration, unnecessary child processes, or unnecessary third-party runtime dependencies.

Prefer bounded temporary computation over persistent storage. Correctness, privacy, deterministic verification, and understandable failure modes take precedence over marginal throughput gains.

## Current architecture

The application uses C#, WPF, .NET 10, System.Text.Json, System.IO.Compression, System.Security.Cryptography, Task/CancellationToken/IProgress, and a deliberately small dependency surface.

Import streams `conversations.json`, reconstructs only the active `current_node` path, classifies message visibility structurally, derives a lightweight metadata index, and releases transcript/raw-record state as soon as practical.

Readable exports rehydrate selected conversations directly from the original source in one pass and verify metadata plus transcript/raw-record fingerprints before finalization. Complete Conversation JSON independently verifies the canonical raw-record fingerprint before output is accepted.

## Continuation protocol

Continuation Markdown uses the current continuity protocol and deterministic `CONTINUITY_TURN` framing. Protocol changes that alter serialized identifiers must be explicitly versioned and covered by golden-output and verifier regressions.

Verification must remain section/state aware so historical transcript text resembling framing syntax, headings, manifests, or endpoint text is treated as content rather than generated structure.

## Security contract

The application must remain local-only and `asInvoker`.

CI must continue to enforce:
- no application networking code or owned runtime TCP/UDP endpoints;
- no Registry/service/startup persistence;
- no script-shell execution or unexpected dynamic/native loading;
- no administrator elevation;
- only the explicit Explorer **Open Folder** child-process action;
- ASLR, DEP/NX, and high-entropy VA;
- zero controlled TEMP/TMP/APPDATA/LOCALAPPDATA/bundle-extraction residue;
- repository identity sanitation;
- deterministic SDK selection and immutable action references.

## Release engineering

The supported package is the managed-compressed, self-contained Windows x64 build containing one application executable and five native WPF runtime libraries, plus required legal/notice files.

Release artifacts must be rebuilt from the exact immutable release tag. Tag, source version, and checked-out commit must agree before packaging.

Required validation includes regression tests, architecture tests, hostile-input tests, reproducibility comparison, startup survival, PE mitigation audit, runtime network audit, controlled-root residue audit, and package hashing.

## Windows UI QA

Before a stable release, manually validate representative scaling levels (100%, 125%, 150%, 200%), keyboard traversal, focus visibility, modal behavior, minimum-window usability, and mixed-DPI monitor movement.

## Tests and private acceptance

Repository tests must use synthetic data only. Never commit real OpenAI account exports, real conversation content, identifying Activity Logs, private continuation archives, or corpus-specific metadata.

Private acceptance may be used locally when behavior changes, but those materials and identifying measurements remain outside Git.

## Change discipline

Parser/schema behavior changes require a demonstrated compatibility, correctness, security, or performance reason. Avoid speculative parser duplication, persistent preloaders, custom allocators, trimming/NativeAOT complexity, or new export formats without a concrete user-facing need.

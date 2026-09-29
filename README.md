# LLM Continuity Toolkit

**Developed by DevMoarRamz**

LLM Continuity Toolkit is a local-first Windows utility for reconstructing, preserving, archiving, and continuing conversations from official OpenAI account data exports.

## Current development line

- Version: **2.4.0**
- Platform: **Windows 10/11 x64**
- Application: **C# / WPF on .NET 10**
- Deployment: **self-contained portable Windows application**
- Processing: **local-only**

The application imports official OpenAI account export data, reconstructs the active `current_node` conversation path, filters internal/non-visible records, and exports selected conversations as Continuation Markdown, Markdown, HTML, plain text, or Complete Conversation JSON.

## Privacy model

- No application network requests.
- No telemetry or analytics.
- No API credentials or account integration.
- No persistent conversation cache or database.
- No background service, updater, tray process, or startup entry.
- Activity Log data remains memory-only unless explicitly saved.
- **Save Redacted...** pseudonymizes registered conversation titles, identifiers, and local filesystem values.
- Real exports, transcripts, generated bundles, and private diagnostics are excluded from the repository.

## Continuation protocol

The current protocol uses product-neutral identifiers: `continuity-conversation-v2`, `continuity-bundle-v2`, `continuity-visible-transcript-v2`, and `CONTINUITY_TURN`.

## Integrity and hardening

The application retains in-memory SHA-256 fingerprints of the visible transcript and complete raw conversation record. Selected hydration/export re-verifies the appropriate fingerprints before output is finalized.

Release validation includes deterministic SDK selection, immutable GitHub Action SHAs, repository identity sanitation, synthetic regression and hostile-input tests, reproducible portable publishing, Windows PE mitigation checks, startup survival, zero application-owned TCP/UDP endpoints while idle, and controlled-root zero-residue auditing.

See [SECURITY.md](SECURITY.md) for the trust model and [DEVELOPMENT.md](DEVELOPMENT.md) for engineering constraints.

## License

LLM Continuity Toolkit is source-available software. See [LICENSE.txt](LICENSE.txt). Microsoft/.NET/WPF runtime components remain governed by their own terms; see [MICROSOFT-RUNTIME-NOTICES.txt](MICROSOFT-RUNTIME-NOTICES.txt).

## Independence

LLM Continuity Toolkit is independently developed and maintained by DevMoarRamz. References to OpenAI describe the supported export source and interoperability target and do not imply affiliation, sponsorship, or endorsement.

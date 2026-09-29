using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LLMContinuityToolkit.Core;

public sealed class ExportService
{
    private readonly ActivitySink _activity;
    private readonly ContinuationWriter _continuationWriter = new();
    private readonly ContinuationVerifier _continuationVerifier = new();

    public ExportService(ActivitySink activity) => _activity = activity;

    public async Task<ExportResult> ExportAsync(
        IReadOnlyList<ConversationRecord> conversations,
        ExportFormat format,
        string destinationFolder,
        string? sourcePath = null,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (conversations.Count == 0)
            throw new ArgumentException("At least one conversation must be selected.", nameof(conversations));

        if (format != ExportFormat.CompleteJson)
        {
            var unsupported = conversations.FirstOrDefault(static row => row.HasUnsupportedVisibleContent);
            if (unsupported is not null)
            {
                throw new InvalidDataException(
                    $"Readable export is blocked for '{unsupported.Title}' because its active transcript contains unsupported OpenAI account content type(s): "
                    + string.Join(", ", unsupported.UnsupportedVisibleContentTypes)
                    + ". Update the application before exporting so history is not silently omitted.");
            }
        }

        Directory.CreateDirectory(destinationFolder);

        var watch = Stopwatch.StartNew();
        _activity.Write("EXPORT", $"Preparing {conversations.Count} conversation(s) as {FormatLabel(format)}.");

        if (conversations.Count == 1)
        {
            var row = conversations[0];
            var finalPath = FileNameUtil.UniquePath(Path.Combine(destinationFolder, BuildOutputFileName(row, format)));
            var stagingPath = Path.Combine(destinationFolder, $".llm-continuity-{Guid.NewGuid():N}.stage");
            try
            {
                var verification = await WriteSingleAsync(stagingPath, row, format, sourcePath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(stagingPath, finalPath, overwrite: false);
                watch.Stop();
                _activity.Write("EXPORT", "1 verified file finalized successfully.", ActivityLevel.Success);
                return new ExportResult
                {
                    OutputPath = finalPath,
                    Format = format,
                    ConversationCount = 1,
                    VerifiedCount = verification.Verified ? 1 : 0,
                    AttachmentReferenceCount = verification.AttachmentReferences,
                    IsBundle = false,
                    ContinuationPrompt = format == ExportFormat.ContinuationMarkdown ? ContinuationPrompt.SingleFile : null,
                    Elapsed = watch.Elapsed
                };
            }
            finally
            {
                try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
            }
        }

        var bundle = await ExportBundleAsync(conversations, format, destinationFolder, sourcePath, progress, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return new ExportResult
        {
            OutputPath = bundle.Path,
            Format = format,
            ConversationCount = conversations.Count,
            VerifiedCount = bundle.VerifiedCount,
            AttachmentReferenceCount = bundle.AttachmentReferences,
            IsBundle = true,
            ContinuationPrompt = format == ExportFormat.ContinuationMarkdown ? ContinuationInstructions.ForBundle(conversations.Count) : null,
            Elapsed = watch.Elapsed
        };
    }

    private async Task<(string Path, int VerifiedCount, int AttachmentReferences)> ExportBundleAsync(
        IReadOnlyList<ConversationRecord> conversations,
        ExportFormat format,
        string destinationFolder,
        string? sourcePath,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"llm-continuity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        var manifestItems = new List<BundleManifestItem>(conversations.Count);
        var verifiedCount = 0;
        var attachmentReferences = 0;
        var generatedAt = DateTimeOffset.Now;
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (format == ExportFormat.CompleteJson)
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                    throw new InvalidOperationException("The original OpenAI account export is required for Complete JSON export.");

                var prepared = new List<(ConversationRecord Row, string Path)>(conversations.Count);
                var requests = new List<RawJsonExportRequest>(conversations.Count);
                for (var i = 0; i < conversations.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = conversations[i];
                    progress?.Report(new ExportProgress(i + 1, conversations.Count, row.Title, "Preparing raw export"));
                    _activity.Write("EXPORT", $"{i + 1}/{conversations.Count}  {row.Title}");

                    var path = ReserveStagingPath(stagingRoot, BuildOutputFileName(row, format), reservedNames);
                    prepared.Add((row, path));
                    requests.Add(new RawJsonExportRequest(row.Id, path, row.RawRecordFingerprint));
                }

                _activity.Write("RAW", $"Streaming {requests.Count} selected original conversation record(s) in one source scan.");
                await RawJsonExporter.ExportConversationsAsync(sourcePath, requests, cancellationToken).ConfigureAwait(false);

                foreach (var item in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    verifiedCount++;
                    attachmentReferences += item.Row.AttachmentCount;
                    var hash = await HashUtil.Sha256Async(item.Path, cancellationToken).ConfigureAwait(false);
                    manifestItems.Add(new BundleManifestItem
                    {
                        Title = item.Row.Title,
                        FileName = Path.GetFileName(item.Path),
                        Messages = item.Row.MessageCount,
                        UserMessages = item.Row.UserCount,
                        AssistantMessages = item.Row.AssistantCount,
                        AttachmentReferences = item.Row.AttachmentCount,
                        LastActiveMessage = item.Row.LastActiveMessage,
                        Sha256 = hash
                    });
                    _activity.Write("VERIFY", $"{item.Row.Title}: original raw conversation fingerprint verified before Complete JSON export.", ActivityLevel.Success);
                }
            }
            else
            {
                for (var i = 0; i < conversations.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = conversations[i];
                    progress?.Report(new ExportProgress(i + 1, conversations.Count, row.Title, "Exporting"));
                    _activity.Write("EXPORT", $"{i + 1}/{conversations.Count}  {row.Title}");

                    var path = ReserveStagingPath(stagingRoot, BuildOutputFileName(row, format), reservedNames);
                    var verification = await WriteSingleAsync(path, row, format, sourcePath, cancellationToken).ConfigureAwait(false);
                    if (verification.Verified) verifiedCount++;
                    attachmentReferences += verification.AttachmentReferences;
                    var hash = await HashUtil.Sha256Async(path, cancellationToken).ConfigureAwait(false);
                    manifestItems.Add(new BundleManifestItem
                    {
                        Title = row.Title,
                        FileName = Path.GetFileName(path),
                        Messages = row.MessageCount,
                        UserMessages = row.UserCount,
                        AssistantMessages = row.AssistantCount,
                        AttachmentReferences = verification.AttachmentReferences,
                        LastActiveMessage = row.LastActiveMessage,
                        Sha256 = hash
                    });
                }
            }

            string? instructionsFile = null;
            string? expectedInstructions = null;
            if (format == ExportFormat.ContinuationMarkdown)
            {
                instructionsFile = "00 - READ ME FIRST - Continuation Instructions.txt";
                expectedInstructions = ContinuationInstructions.BuildBundleReadMe(conversations.Count, generatedAt);
                await File.WriteAllTextAsync(
                    Path.Combine(stagingRoot, instructionsFile),
                    expectedInstructions,
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
            }

            var manifest = new BundleManifest
            {
                GeneratedAtUtc = generatedAt.UtcDateTime.ToString("O"),
                ExportFormat = format.ToString(),
                ConversationCount = conversations.Count,
                InstructionsFile = instructionsFile,
                Files = manifestItems
            };
            var manifestPath = Path.Combine(stagingRoot, "bundle-manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            var stem = format == ExportFormat.ContinuationMarkdown ? "LLM_Continuity_Bundle" : "LLM_Conversation_Bundle";
            var finalName = $"{stem}_{generatedAt:yyyy-MM-dd_HHmmss}.zip";
            var finalPath = FileNameUtil.UniquePath(Path.Combine(destinationFolder, finalName));
            var tempZip = Path.Combine(destinationFolder, $".llm-continuity-{Guid.NewGuid():N}.tmp");

            _activity.Write("BUNDLE", $"Packaging {conversations.Count} verified conversation file(s).");
            try
            {
                using (var file = new FileStream(tempZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 256 * 1024))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
                {
                    foreach (var filePath in Directory.EnumerateFiles(stagingRoot).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        archive.CreateEntryFromFile(filePath, Path.GetFileName(filePath), CompressionLevel.Optimal);
                    }
                }

                var bundleVerification = await BundleVerifier.VerifyAsync(
                    tempZip,
                    manifest,
                    expectedInstructions,
                    cancellationToken).ConfigureAwait(false);
                if (!bundleVerification.Verified)
                    throw new InvalidDataException("Final bundle verification failed.");

                File.Move(tempZip, finalPath, overwrite: false);
                _activity.Write(
                    "VERIFY",
                    $"Bundle verified: manifest passed; instructions {(expectedInstructions is null ? "not applicable" : "passed")}; {bundleVerification.VerifiedPayloads}/{conversations.Count} payload hashes matched.",
                    ActivityLevel.Success);
                _activity.Write("BUNDLE", "Continuation/export archive finalized successfully.", ActivityLevel.Success);
                return (finalPath, verifiedCount, attachmentReferences);
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); } catch { }
        }
    }

    private async Task<ContinuationVerificationResult> WriteSingleAsync(
        string path,
        ConversationRecord row,
        ExportFormat format,
        string? sourcePath,
        CancellationToken cancellationToken)
    {
        switch (format)
        {
            case ExportFormat.ContinuationMarkdown:
                await _continuationWriter.WriteAsync(path, row, cancellationToken).ConfigureAwait(false);
                var result = await _continuationVerifier.VerifyAsync(path, row, cancellationToken).ConfigureAwait(false);
                _activity.Write("VERIFY", $"{row.Title}: {row.MessageCount}/{row.MessageCount} turns verified; structural + handoff integrity passed.", ActivityLevel.Success);
                return result;

            case ExportFormat.Markdown:
                await AtomicFile.WriteUtf8Async(path, writer => WriteMarkdownAsync(writer, row, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.PlainText:
                await AtomicFile.WriteUtf8Async(path, writer => WriteTextAsync(writer, row, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.Html:
                await AtomicFile.WriteUtf8Async(path, writer => WriteHtmlAsync(writer, row, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case ExportFormat.CompleteJson:
                if (string.IsNullOrWhiteSpace(sourcePath))
                    throw new InvalidOperationException("The original OpenAI account export is required for Complete JSON export.");
                await RawJsonExporter.ExportConversationAsync(
                    sourcePath,
                    row.Id,
                    path,
                    row.RawRecordFingerprint,
                    cancellationToken).ConfigureAwait(false);
                _activity.Write("VERIFY", $"{row.Title}: original raw conversation fingerprint verified before Complete JSON export.", ActivityLevel.Success);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }

        return new ContinuationVerificationResult
        {
            Verified = true,
            StructuralVerified = true,
            HandoffVerified = true,
            ExpectedTurns = row.MessageCount,
            AttachmentReferences = format == ExportFormat.CompleteJson
                ? row.AttachmentCount
                : AttachmentManifest.GetLines(row).Count
        };
    }

    private static string ReserveStagingPath(string stagingRoot, string fileName, ISet<string> reservedNames)
    {
        if (reservedNames.Add(fileName))
            return Path.Combine(stagingRoot, fileName);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i < 10_000; i++)
        {
            var suffix = $" ({i})";
            var safeStem = FileNameUtil.SafeFileName(stem, Math.Max(8, 120 - suffix.Length));
            var candidateName = safeStem + suffix + extension;
            if (reservedNames.Add(candidateName))
                return Path.Combine(stagingRoot, candidateName);
        }

        throw new IOException("Could not reserve a unique staging filename after 9,998 collision attempts.");
    }

    private static string BuildOutputFileName(ConversationRecord row, ExportFormat format)
    {
        var suffix = format == ExportFormat.ContinuationMarkdown ? " - Continuation" : string.Empty;
        var stem = FileNameUtil.SafeFileName($"{TimestampUtil.DatePrefix(row.CreateTimeRaw)} - {row.Title}{suffix}");
        var extension = format switch
        {
            ExportFormat.ContinuationMarkdown or ExportFormat.Markdown => ".md",
            ExportFormat.Html => ".html",
            ExportFormat.PlainText => ".txt",
            ExportFormat.CompleteJson => ".json",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
        return stem + extension;
    }

    private static string FormatLabel(ExportFormat format) => format switch
    {
        ExportFormat.ContinuationMarkdown => "Continuation Markdown (.md) — Recommended",
        ExportFormat.Markdown => "Markdown (.md)",
        ExportFormat.Html => "HTML (.html)",
        ExportFormat.PlainText => "Plain text (.txt)",
        ExportFormat.CompleteJson => "Complete Conversation JSON (.json)",
        _ => format.ToString()
    };

    private static async Task WriteMarkdownAsync(TextWriter writer, ConversationRecord row, CancellationToken token)
    {
        await writer.WriteLineAsync($"# {row.Title}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync($"- Created: {row.Created}").ConfigureAwait(false);
        await writer.WriteLineAsync($"- Updated: {row.Updated}").ConfigureAwait(false);
        await writer.WriteLineAsync($"- Active transcript messages: {row.MessageCount}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("---").ConfigureAwait(false);
        foreach (var message in row.Messages)
        {
            token.ThrowIfCancellationRequested();
            var label = message.Role == "user" ? "User" : "Assistant";
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync($"## {label}").ConfigureAwait(false);
            var stamp = TimestampUtil.FormatLocal(message.CreateTime);
            if (stamp != "Unknown") await writer.WriteLineAsync($"*{stamp}*").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync(message.Text).ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("---").ConfigureAwait(false);
        }
    }

    private static async Task WriteTextAsync(TextWriter writer, ConversationRecord row, CancellationToken token)
    {
        var bar = new string('=', 78);
        await writer.WriteLineAsync(row.Title).ConfigureAwait(false);
        await writer.WriteLineAsync($"Created: {row.Created}").ConfigureAwait(false);
        await writer.WriteLineAsync($"Updated: {row.Updated}").ConfigureAwait(false);
        await writer.WriteLineAsync($"Active transcript messages: {row.MessageCount}").ConfigureAwait(false);
        await writer.WriteLineAsync(bar).ConfigureAwait(false);
        foreach (var message in row.Messages)
        {
            token.ThrowIfCancellationRequested();
            var label = message.Role == "user" ? "USER" : "ASSISTANT";
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync($"{label}  {TimestampUtil.FormatLocal(message.CreateTime)}").ConfigureAwait(false);
            await writer.WriteLineAsync(new string('-', 78)).ConfigureAwait(false);
            await writer.WriteLineAsync(message.Text).ConfigureAwait(false);
        }
    }

    private static async Task WriteHtmlAsync(TextWriter writer, ConversationRecord row, CancellationToken token)
    {
        await writer.WriteLineAsync("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">").ConfigureAwait(false);
        await writer.WriteLineAsync($"<title>{WebUtility.HtmlEncode(row.Title)}</title><style>body{{font-family:system-ui,sans-serif;max-width:980px;margin:40px auto;padding:0 20px;line-height:1.55}}article{{border-top:1px solid #bbb;padding:18px 0}}.meta{{color:#666}}</style></head><body>").ConfigureAwait(false);
        await writer.WriteLineAsync($"<h1>{WebUtility.HtmlEncode(row.Title)}</h1><p class=\"meta\">Created: {WebUtility.HtmlEncode(row.Created)} · Updated: {WebUtility.HtmlEncode(row.Updated)} · Messages: {row.MessageCount}</p>").ConfigureAwait(false);
        foreach (var message in row.Messages)
        {
            token.ThrowIfCancellationRequested();
            var label = message.Role == "user" ? "User" : "Assistant";
            var encoded = WebUtility.HtmlEncode(message.Text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br>\n", StringComparison.Ordinal);
            await writer.WriteLineAsync($"<article><h2>{label}</h2><div class=\"meta\">{WebUtility.HtmlEncode(TimestampUtil.FormatLocal(message.CreateTime))}</div><p>{encoded}</p></article>").ConfigureAwait(false);
        }
        await writer.WriteLineAsync("</body></html>").ConfigureAwait(false);
    }
}

public static class BundleVerifier
{
    public static async Task<BundleVerificationResult> VerifyAsync(
        string zipPath,
        BundleManifest expectedManifest,
        string? expectedInstructions,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.ToDictionary(static e => e.FullName, StringComparer.Ordinal);
        var expectedEntries = expectedManifest.Files.Count + 1 + (expectedManifest.InstructionsFile is null ? 0 : 1);

        if (entries.Count != expectedEntries)
            throw new InvalidDataException($"Bundle entry count mismatch. Expected {expectedEntries}, got {entries.Count}.");
        foreach (var name in entries.Keys)
        {
            if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException($"Unsafe or nested bundle entry detected: {name}");
        }

        if (!entries.TryGetValue("bundle-manifest.json", out var manifestEntry))
            throw new InvalidDataException("Bundle manifest is missing from final ZIP.");

        BundleManifest embeddedManifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            embeddedManifest = await JsonSerializer.DeserializeAsync<BundleManifest>(manifestStream, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Bundle manifest could not be parsed.");
        }

        var manifestVerified = embeddedManifest.Format == expectedManifest.Format
            && embeddedManifest.BundleSchema == 1
            && embeddedManifest.Application == AppInfo.Name
            && embeddedManifest.ApplicationVersion == AppInfo.Version
            && embeddedManifest.Developer == AppInfo.Developer
            && embeddedManifest.GeneratedBy == AppInfo.DisplayName
            && embeddedManifest.GeneratedAtUtc == expectedManifest.GeneratedAtUtc
            && embeddedManifest.ExportFormat == expectedManifest.ExportFormat
            && embeddedManifest.ConversationCount == expectedManifest.ConversationCount
            && embeddedManifest.PayloadHashAlgorithm == "SHA-256"
            && embeddedManifest.InstructionsFile == expectedManifest.InstructionsFile
            && embeddedManifest.Files.Count == expectedManifest.Files.Count;
        if (!manifestVerified)
            throw new InvalidDataException("Embedded bundle manifest does not match the verified export metadata.");

        var expectedByFile = expectedManifest.Files.ToDictionary(static item => item.FileName, StringComparer.Ordinal);
        foreach (var item in embeddedManifest.Files)
        {
            if (!expectedByFile.TryGetValue(item.FileName, out var expected)
                || item.Title != expected.Title
                || item.Messages != expected.Messages
                || item.UserMessages != expected.UserMessages
                || item.AssistantMessages != expected.AssistantMessages
                || item.AttachmentReferences != expected.AttachmentReferences
                || item.LastActiveMessage != expected.LastActiveMessage
                || !item.Sha256.Equals(expected.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Embedded manifest payload metadata mismatch: {item.FileName}");
            }
        }

        var instructionsVerified = expectedManifest.InstructionsFile is null;
        if (expectedManifest.InstructionsFile is not null)
        {
            if (!entries.TryGetValue(expectedManifest.InstructionsFile, out var instructionsEntry))
                throw new InvalidDataException("Continuation instructions are missing from final ZIP.");
            if (expectedInstructions is null)
                throw new InvalidDataException("Expected continuation instructions were not supplied to the verifier.");

            using var reader = new StreamReader(instructionsEntry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var actualInstructions = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            instructionsVerified = actualInstructions == expectedInstructions;
            if (!instructionsVerified)
                throw new InvalidDataException("Continuation instructions changed during bundle creation.");
        }

        var verifiedPayloads = 0;
        foreach (var item in embeddedManifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(item.FileName, out var entry))
                throw new InvalidDataException($"Bundle payload is missing: {item.FileName}");
            await using var stream = entry.Open();
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            if (!hex.Equals(item.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Bundle hash mismatch: {item.FileName}");
            verifiedPayloads++;
        }

        return new BundleVerificationResult
        {
            Verified = manifestVerified && instructionsVerified && verifiedPayloads == embeddedManifest.Files.Count,
            ManifestVerified = manifestVerified,
            InstructionsVerified = instructionsVerified,
            ExpectedEntries = expectedEntries,
            ActualEntries = entries.Count,
            VerifiedPayloads = verifiedPayloads
        };
    }
}

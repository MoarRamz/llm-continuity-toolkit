using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace LLMContinuityToolkit.Core;

public sealed class ChatExportReader
{
    private const long MaxConversationJsonBytes = 8L * 1024 * 1024 * 1024;
    private const int MaxMappingNodes = 2_000_000;
    private const int MaxVisibleMessageChars = 64 * 1024 * 1024;
    private const double MaxSuspiciousCompressionRatio = 1000d;
    private readonly ActivitySink _activity;

    public ChatExportReader(ActivitySink activity) => _activity = activity;

    public Task<ImportResult> ReadAsync(
        string sourcePath,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ReadCoreAsync(sourcePath, metadataOnly: false, progress, cancellationToken);

    public Task<ImportResult> ReadMetadataAsync(
        string sourcePath,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ReadCoreAsync(sourcePath, metadataOnly: true, progress, cancellationToken);

    public async Task<IReadOnlyList<ConversationRecord>> HydrateSelectedAsync(
        string sourcePath,
        IReadOnlyList<ConversationRecord> selectedMetadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (selectedMetadata.Count == 0)
            return Array.Empty<ConversationRecord>();

        var ordered = new ConversationRecord[selectedMetadata.Count];
        var requested = new Dictionary<string, ConversationRecord>(StringComparer.Ordinal);
        for (var i = 0; i < selectedMetadata.Count; i++)
        {
            var metadata = selectedMetadata[i];
            if (metadata.HasUnsupportedVisibleContent)
            {
                throw new InvalidDataException(
                    $"Readable export is blocked for '{metadata.Title}' because its active transcript contains unsupported OpenAI account content type(s): "
                    + string.Join(", ", metadata.UnsupportedVisibleContentTypes)
                    + ". Update the application before exporting this conversation so history is not silently omitted.");
            }

            if (metadata.HasTranscript)
            {
                ordered[i] = metadata;
                continue;
            }

            if (!requested.TryAdd(metadata.Id, metadata))
                throw new InvalidDataException($"Duplicate selected conversation ID '{metadata.Id}' cannot be hydrated safely.");
        }

        if (requested.Count == 0)
            return ordered;

        var watch = Stopwatch.StartNew();
        var compatibility = new CompatibilityReport();
        var hydratedById = new Dictionary<string, ConversationRecord>(requested.Count, StringComparer.Ordinal);

        _activity.Write("HYDRATE", $"Directly reconstructing {requested.Count} selected transcript(s) in one source scan.");
        _activity.Write("MEMORY", OperationMemory.Snapshot("Before direct selected transcript hydration"));

        await using var source = await OpenConversationJsonStreamAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        await foreach (var element in EnumerateConversationElementsAsync(source, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var id = GetString(element, "id");
            if (id is null || !requested.TryGetValue(id, out var metadata) || hydratedById.ContainsKey(id))
                continue;

            compatibility.ConversationRecordsFound++;
            var hydrated = BuildConversation(element, compatibility, cancellationToken)
                ?? throw new InvalidDataException($"Selected conversation '{metadata.Title}' could not be reconstructed from the source export.");
            hydrated.RawRecordFingerprint = RawRecordFingerprint.Compute(element);
            compatibility.ConversationsPrepared++;
            VerifyParity(metadata, hydrated);
            hydratedById.Add(id, hydrated);

            if (hydratedById.Count == requested.Count)
                break;
        }

        if (hydratedById.Count != requested.Count)
        {
            var missing = requested.Keys.Where(id => !hydratedById.ContainsKey(id)).Take(5).ToArray();
            throw new InvalidDataException(
                $"Could not reconstruct {requested.Count - hydratedById.Count} selected conversation(s) from the source export. Missing IDs: {string.Join(", ", missing)}");
        }

        for (var i = 0; i < selectedMetadata.Count; i++)
        {
            if (ordered[i] is not null)
                continue;
            ordered[i] = hydratedById[selectedMetadata[i].Id];
        }

        watch.Stop();
        _activity.Write(
            "HYDRATE",
            $"{requested.Count} selected transcript(s) directly reconstructed and metadata/content fingerprint verified in {watch.Elapsed.TotalSeconds:F2} s.",
            ActivityLevel.Success);
        _activity.Write("MEMORY", OperationMemory.Snapshot("After direct selected transcript hydration"));
        return ordered;
    }

    private async Task<ImportResult> ReadCoreAsync(
        string sourcePath,
        bool metadataOnly,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var totalWatch = Stopwatch.StartNew();
        var compatibility = new CompatibilityReport();

        _activity.Write("MEMORY", OperationMemory.Snapshot("Before source read"));
        var sourceWatch = Stopwatch.StartNew();
        await using var source = await OpenConversationJsonStreamAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        sourceWatch.Stop();
        _activity.Write("PERF", $"Source opened in {sourceWatch.Elapsed.TotalSeconds:F2} s", ActivityLevel.Performance);

        var records = new List<ConversationRecord>(128);
        var seenStableIds = new HashSet<string>(StringComparer.Ordinal);
        var indexWatch = Stopwatch.StartNew();
        var index = 0;
        var transcriptFallbacks = 0;

        await foreach (var element in EnumerateConversationElementsAsync(source, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            compatibility.ConversationRecordsFound++;

            var stableSourceId = GetString(element, "id");
            if (!string.IsNullOrWhiteSpace(stableSourceId) && !seenStableIds.Add(stableSourceId))
            {
                compatibility.DuplicateConversationIds++;
                throw new InvalidDataException(
                    $"Duplicate OpenAI account conversation ID '{stableSourceId}' was found. Import stopped because later lazy hydration would be ambiguous.");
            }

            try
            {
                var record = BuildConversation(element, compatibility, cancellationToken);
                if (record is null)
                {
                    compatibility.MalformedConversationRecords++;
                    continue;
                }

                record.RawRecordFingerprint = RawRecordFingerprint.Compute(element);

                if (metadataOnly && !string.IsNullOrWhiteSpace(stableSourceId))
                {
                    record = ToMetadataRecord(record);
                }
                else if (metadataOnly && record.MessageCount > 0)
                {
                    transcriptFallbacks++;
                }

                records.Add(record);
                compatibility.ConversationsPrepared++;
                index++;
                progress?.Report(new ImportProgress("Indexing", index, 0, $"{record.Title} — {record.MessageCount} visible messages"));
                _activity.Write("INDEX", $"{index}  {record.Title} — {record.MessageCount} visible messages");
                if (record.HasUnsupportedVisibleContent)
                {
                    _activity.Write(
                        "COMPAT",
                        $"{record.Title}: readable exports blocked until support is added for active content type(s): {string.Join(", ", record.UnsupportedVisibleContentTypes)}.",
                        ActivityLevel.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or InvalidDataException)
            {
                compatibility.MalformedConversationRecords++;
                _activity.Write("COMPAT", $"Skipped malformed conversation record: {ex.Message}", ActivityLevel.Warning);
            }
        }

        indexWatch.Stop();
        totalWatch.Stop();

        records.Sort(static (a, b) =>
        {
            var aTime = a.UpdateTimeRaw ?? a.CreateTimeRaw ?? 0;
            var bTime = b.UpdateTimeRaw ?? b.CreateTimeRaw ?? 0;
            var cmp = bTime.CompareTo(aTime);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title);
        });

        _activity.Write("PERF", $"Transcript indexing completed in {indexWatch.Elapsed.TotalSeconds:F2} s", ActivityLevel.Performance);
        _activity.Write("MEMORY", OperationMemory.Snapshot("After streaming transcript indexing"));
        _activity.Write("COMPAT", BuildCompatibilitySummary(compatibility),
            compatibility.MalformedConversationRecords == 0
            && compatibility.BrokenActivePaths == 0
            && compatibility.UnknownStructuredContentTypes == 0
            && compatibility.DuplicateConversationIds == 0
                ? ActivityLevel.Success
                : ActivityLevel.Warning);
        if (metadataOnly)
        {
            _activity.Write(
                "MEMORY",
                transcriptFallbacks == 0
                    ? $"Streaming metadata-only index retained for {records.Count} conversation(s); transcript bodies were not retained across records."
                    : $"Streaming metadata-only index retained for {records.Count - transcriptFallbacks} conversation(s); {transcriptFallbacks} record(s) without stable IDs retained transcripts for safe export.",
                transcriptFallbacks == 0 ? ActivityLevel.Success : ActivityLevel.Warning);
        }
        _activity.Write("PERF", $"Total import pipeline: {totalWatch.Elapsed.TotalSeconds:F2} s", ActivityLevel.Performance);

        return new ImportResult
        {
            Conversations = records,
            Compatibility = compatibility,
            SourcePreparationTime = sourceWatch.Elapsed,
            TranscriptIndexingTime = indexWatch.Elapsed,
            TotalTime = totalWatch.Elapsed
        };
    }

    private async Task<Stream> OpenConversationJsonStreamAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            _activity.Write("IMPORT", $"Opening conversation JSON: {Path.GetFileName(sourcePath)}");
            return new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        _activity.Write("IMPORT", $"Opening OpenAI account export: {Path.GetFileName(sourcePath)}");
        var zipStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > 100_000)
                throw new InvalidDataException("The ZIP contains an unexpectedly large number of entries.");

            var matches = archive.Entries
                .Where(static entry => entry.FullName.Equals("conversations.json", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.EndsWith("/conversations.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length != 1)
                throw new InvalidDataException($"Expected exactly one conversations.json entry, found {matches.Length}.");

            var entry = matches[0];
            if (entry.Length <= 0)
                throw new InvalidDataException("conversations.json is empty.");
            if (entry.Length > MaxConversationJsonBytes)
                throw new InvalidDataException("conversations.json exceeds the supported safety limit.");
            if (entry.CompressedLength <= 0)
                throw new InvalidDataException("conversations.json has an invalid compressed length.");

            var compressionRatio = entry.Length / (double)entry.CompressedLength;
            if (entry.Length >= 16L * 1024 * 1024 && compressionRatio > MaxSuspiciousCompressionRatio)
                throw new InvalidDataException($"conversations.json has a suspicious compression ratio ({compressionRatio:F0}:1).");

            _activity.Write("ZIP", $"Archive opened; conversations.json is {entry.Length / 1024d / 1024d:F1} MB; compression ratio {compressionRatio:F1}:1.");
            return new OwnedZipEntryStream(entry.Open(), archive, zipStream);
        }
        catch
        {
            archive?.Dispose();
            await zipStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async IAsyncEnumerable<JsonElement> EnumerateConversationElementsAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prefix = new List<byte>(16);
        var one = new byte[1];
        byte first;
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("Conversation JSON is empty.");
            prefix.Add(one[0]);
            if (!char.IsWhiteSpace((char)one[0]))
            {
                first = one[0];
                break;
            }
        }

        await using var replay = new PrefixStream(prefix.ToArray(), stream);
        if (first == (byte)'[')
        {
            await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(replay, cancellationToken: cancellationToken).ConfigureAwait(false))
                yield return item;
            yield break;
        }

        using var document = await JsonDocument.ParseAsync(replay, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikeConversation(root))
            {
                yield return root.Clone();
                yield break;
            }

            foreach (var wrapperName in new[] { "conversations", "items", "data" })
            {
                if (!root.TryGetProperty(wrapperName, out var wrapped) || wrapped.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in wrapped.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item.Clone();
                }
                yield break;
            }
        }

        throw new InvalidDataException("The JSON does not contain a recognized OpenAI account conversation collection.");
    }

    private static bool LooksLikeConversation(JsonElement element)
        => element.TryGetProperty("mapping", out _)
            || element.TryGetProperty("current_node", out _)
            || element.TryGetProperty("title", out _);

    private static ConversationRecord? BuildConversation(
        JsonElement conversation,
        CompatibilityReport compatibility,
        CancellationToken cancellationToken)
    {
        if (conversation.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetString(conversation, "id") ?? Guid.NewGuid().ToString("N");
        var title = GetString(conversation, "title") ?? "Untitled Conversation";
        var createTime = GetNumber(conversation, "create_time");
        var updateTime = GetNumber(conversation, "update_time");

        if (!conversation.TryGetProperty("mapping", out var mapping) || mapping.ValueKind != JsonValueKind.Object)
        {
            compatibility.MissingMapping++;
            return EmptyRecord(id, title, createTime, updateTime);
        }

        var current = GetString(conversation, "current_node");
        if (string.IsNullOrWhiteSpace(current))
        {
            compatibility.MissingCurrentNode++;
            return EmptyRecord(id, title, createTime, updateTime);
        }

        var mappingCount = mapping.EnumerateObject().Count();
        if (mappingCount > MaxMappingNodes)
            throw new InvalidDataException($"Conversation '{title}' exceeds the supported mapping-node safety limit.");

        var nodeMap = new Dictionary<string, JsonElement>(mappingCount, StringComparer.Ordinal);
        foreach (var property in mapping.EnumerateObject())
            nodeMap[property.Name] = property.Value;

        var path = new List<JsonElement>(Math.Min(nodeMap.Count, 4096));
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(current))
            {
                compatibility.ActivePathCycles++;
                break;
            }
            if (!nodeMap.TryGetValue(current, out var node))
            {
                compatibility.BrokenActivePaths++;
                break;
            }
            path.Add(node);
            current = GetString(node, "parent");
        }
        path.Reverse();

        var messages = new List<ConversationMessage>(path.Count);
        var userCount = 0;
        var assistantCount = 0;
        var attachmentCount = 0;
        var unsupportedVisibleTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!node.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;

            var classification = MessageVisibilityClassifier.Classify(message);
            if (!classification.IsVisible)
            {
                MessageVisibilityClassifier.CountExclusion(classification.Disposition, compatibility);
                continue;
            }

            var content = MessageContentExtractor.Extract(message, compatibility, cancellationToken);
            foreach (var type in content.UnknownVisibleContentTypes)
                unsupportedVisibleTypes.Add(type);

            if (string.IsNullOrWhiteSpace(content.Text))
            {
                compatibility.SkippedEmptyMessages++;
                continue;
            }
            if (content.Text.Length > MaxVisibleMessageChars)
                throw new InvalidDataException($"Conversation '{title}' contains a visible message exceeding the supported safety limit.");

            var turn = messages.Count + 1;
            var created = GetNumber(message, "create_time");
            messages.Add(new ConversationMessage(turn, classification.Role, content.Text, created, content.AttachmentCount));
            attachmentCount += content.AttachmentCount;
            if (classification.Role == "user") userCount++; else assistantCount++;
        }

        return new ConversationRecord
        {
            Id = id,
            Title = title,
            CreateTimeRaw = createTime,
            UpdateTimeRaw = updateTime,
            Messages = messages,
            UserCount = userCount,
            AssistantCount = assistantCount,
            AttachmentCount = attachmentCount,
            UnsupportedVisibleContentTypes = unsupportedVisibleTypes.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static ConversationRecord ToMetadataRecord(ConversationRecord source)
        => new()
        {
            Id = source.Id,
            Title = source.Title,
            CreateTimeRaw = source.CreateTimeRaw,
            UpdateTimeRaw = source.UpdateTimeRaw,
            Messages = Array.Empty<ConversationMessage>(),
            MessageCount = source.MessageCount,
            UserCount = source.UserCount,
            AssistantCount = source.AssistantCount,
            AttachmentCount = source.AttachmentCount,
            LastActiveMessageTimeRaw = source.LastActiveMessageTimeRaw,
            FinalHistoricalRole = source.FinalHistoricalRole,
            TranscriptFingerprint = TranscriptFingerprint.Compute(source.Messages),
            RawRecordFingerprint = source.RawRecordFingerprint,
            UnsupportedVisibleContentTypes = source.UnsupportedVisibleContentTypes
        };

    private static void VerifyParity(ConversationRecord metadata, ConversationRecord hydrated)
    {
        var hydratedFingerprint = metadata.TranscriptFingerprint is null
            ? null
            : TranscriptFingerprint.Compute(hydrated.Messages);
        var unsupportedMatch = metadata.UnsupportedVisibleContentTypes.SequenceEqual(
            hydrated.UnsupportedVisibleContentTypes,
            StringComparer.OrdinalIgnoreCase);

        if (metadata.MessageCount != hydrated.MessageCount
            || metadata.UserCount != hydrated.UserCount
            || metadata.AssistantCount != hydrated.AssistantCount
            || metadata.AttachmentCount != hydrated.AttachmentCount
            || !string.Equals(metadata.FinalHistoricalRole, hydrated.FinalHistoricalRole, StringComparison.Ordinal)
            || metadata.LastActiveMessageTimeRaw != hydrated.LastActiveMessageTimeRaw
            || !unsupportedMatch
            || (metadata.TranscriptFingerprint is not null
                && !string.Equals(metadata.TranscriptFingerprint, hydratedFingerprint, StringComparison.Ordinal))
            || (metadata.RawRecordFingerprint is not null
                && !string.Equals(metadata.RawRecordFingerprint, hydrated.RawRecordFingerprint, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Transcript hydration parity failed for '{metadata.Title}'. The source export may have changed since it was indexed. "
                + $"Index {metadata.MessageCount}/{metadata.UserCount}/{metadata.AssistantCount}/{metadata.AttachmentCount}; "
                + $"hydrated {hydrated.MessageCount}/{hydrated.UserCount}/{hydrated.AssistantCount}/{hydrated.AttachmentCount}.");
        }
    }

    private static ConversationRecord EmptyRecord(string id, string title, double? createTime, double? updateTime)
        => new()
        {
            Id = id,
            Title = title,
            CreateTimeRaw = createTime,
            UpdateTimeRaw = updateTime,
            Messages = Array.Empty<ConversationMessage>()
        };

    internal static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    internal static double? GetNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static string BuildCompatibilitySummary(CompatibilityReport report)
        => $"Records {report.ConversationRecordsFound}; prepared {report.ConversationsPrepared}; "
         + $"excluded hidden {report.SkippedInvisibleMessages}, tool {report.SkippedToolDirectedMessages}, "
         + $"analysis {report.SkippedAnalysisMessages}, reasoning {report.SkippedStructuredReasoningMessages}, "
         + $"reasoning recaps {report.SkippedReasoningRecaps}, empty {report.SkippedEmptyMessages}; "
         + $"duplicate IDs {report.DuplicateConversationIds}; missing current_node {report.MissingCurrentNode}; missing mapping {report.MissingMapping}; "
         + $"broken active paths {report.BrokenActivePaths}; cycles {report.ActivePathCycles}; "
         + $"unknown structured types {report.UnknownStructuredContentTypes}; malformed {report.MalformedConversationRecords}.";

    private sealed class PrefixStream : Stream
    {
        private readonly MemoryStream _prefix;
        private readonly Stream _inner;

        public PrefixStream(byte[] prefix, Stream inner)
        {
            _prefix = new MemoryStream(prefix, writable: false);
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _prefix.Read(buffer, offset, count);
            return read > 0 ? read : _inner.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            var read = _prefix.Read(buffer);
            return read > 0 ? read : _inner.Read(buffer);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _prefix.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return read > 0 ? read : await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _prefix.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _prefix.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class OwnedZipEntryStream : Stream
    {
        private readonly Stream _inner;
        private readonly ZipArchive _archive;
        private readonly Stream _zipStream;

        public OwnedZipEntryStream(Stream inner, ZipArchive archive, Stream zipStream)
        {
            _inner = inner;
            _archive = archive;
            _zipStream = zipStream;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _archive.Dispose();
                _zipStream.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _archive.Dispose();
            await _zipStream.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}

internal static class MessageContentExtractor
{
    private static readonly HashSet<string> IgnoredStructuredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "reasoning", "thought", "thoughts", "analysis", "tool", "computer", "system", "model_editable_context", "reasoning_recap"
    };

    private static readonly HashSet<string> KnownVisibleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "multimodal_text"
    };

    internal sealed record Result(string Text, int AttachmentCount, IReadOnlyList<string> UnknownVisibleContentTypes);

    public static Result Extract(JsonElement message, CompatibilityReport compatibility, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("content", out var content)
            || content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new Result(string.Empty, 0, Array.Empty<string>());

        var output = new List<string>();
        var seenMarkers = new HashSet<string>(StringComparer.Ordinal);
        var unknownTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var attachmentCount = 0;

        if (content.ValueKind == JsonValueKind.Object)
        {
            var rootType = FirstString(content, "content_type", "type");
            if (!string.IsNullOrWhiteSpace(rootType)
                && !KnownVisibleTypes.Contains(rootType)
                && !IgnoredStructuredTypes.Contains(rootType)
                && !rootType.Contains("image", StringComparison.OrdinalIgnoreCase)
                && !rootType.Contains("audio", StringComparison.OrdinalIgnoreCase))
            {
                compatibility.AddUnknownContentType(rootType);
                unknownTypes.Add(rootType);
            }
        }

        if (content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddPart(PartToText(part, compatibility, unknownTypes), output, seenMarkers, ref attachmentCount);
            }
        }

        if (output.Count == 0 && content.ValueKind == JsonValueKind.Object)
        {
            var contentType = FirstString(content, "content_type", "type") ?? string.Empty;
            if (!IgnoredStructuredTypes.Contains(contentType))
            {
                var direct = FirstString(content, "text", "content", "caption");
                if (!string.IsNullOrWhiteSpace(direct))
                    output.Add(direct);
            }
        }

        if (message.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("attachments", out var attachments)
            && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddPart(StructuredMarker(attachment, compatibility, unknownTypes), output, seenMarkers, ref attachmentCount);
            }
        }

        if (output.Count == 0)
            AddPart(StructuredMarker(content, compatibility, unknownTypes), output, seenMarkers, ref attachmentCount);

        return new Result(
            string.Join(Environment.NewLine + Environment.NewLine, output),
            attachmentCount,
            unknownTypes.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string PartToText(JsonElement part, CompatibilityReport compatibility, ISet<string> unknownTypes)
    {
        if (part.ValueKind == JsonValueKind.String)
            return part.GetString() ?? string.Empty;
        if (part.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return part.GetRawText();
        if (part.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var direct = FirstString(part, "text", "content", "caption");
        return !string.IsNullOrWhiteSpace(direct) ? direct : StructuredMarker(part, compatibility, unknownTypes);
    }

    private static string StructuredMarker(JsonElement part, CompatibilityReport compatibility, ISet<string> unknownTypes)
    {
        if (part.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var name = FirstString(part, "file_name", "filename", "name", "title");
        var mime = FirstString(part, "mime_type") ?? string.Empty;
        var type = FirstString(part, "content_type", "type") ?? string.Empty;
        var asset = FirstString(part, "asset_pointer", "image_asset_pointer");

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || type.Contains("image", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(name) ? "[Uploaded image]" : $"[Uploaded image: {name}]";

        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || type.Contains("audio", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(name) ? "[Audio attachment]" : $"[Audio attachment: {name}]";

        if (!string.IsNullOrWhiteSpace(name))
            return $"[Uploaded file: {name}]";

        if (!string.IsNullOrWhiteSpace(asset))
            return "[Uploaded attachment reference]";

        if (!string.IsNullOrWhiteSpace(type))
        {
            if (IgnoredStructuredTypes.Contains(type))
                return string.Empty;
            if (!KnownVisibleTypes.Contains(type))
            {
                compatibility.AddUnknownContentType(type);
                unknownTypes.Add(type);
                return $"[Structured content: {type}]";
            }
        }

        return string.Empty;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        return null;
    }

    private static void AddPart(
        string text,
        ICollection<string> output,
        ISet<string> seenMarkers,
        ref int attachmentCount)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal))
        {
            if (!seenMarkers.Add(text))
                return;
            output.Add(text);
            if (IsAttachmentMarker(text))
                attachmentCount++;
            return;
        }

        output.Add(text);
    }

    private static bool IsAttachmentMarker(string text)
        => text.StartsWith("[Uploaded image", StringComparison.Ordinal)
            || text.StartsWith("[Uploaded file", StringComparison.Ordinal)
            || text.StartsWith("[Uploaded attachment reference", StringComparison.Ordinal)
            || text.StartsWith("[Audio attachment", StringComparison.Ordinal)
            || text.StartsWith("[Structured content", StringComparison.Ordinal);
}

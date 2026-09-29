using System.IO.Compression;
using System.Text.Json;

namespace LLMContinuityToolkit.Core;

internal sealed record RawJsonExportRequest(
    string ConversationId,
    string DestinationPath,
    string? ExpectedRawFingerprint);

internal static class RawJsonExporter
{
    private const long MaxConversationJsonBytes = 8L * 1024 * 1024 * 1024;
    private const double MaxSuspiciousCompressionRatio = 1000d;

    public static Task ExportConversationAsync(
        string sourcePath,
        string conversationId,
        string destinationPath,
        CancellationToken cancellationToken)
        => ExportConversationAsync(sourcePath, conversationId, destinationPath, expectedRawFingerprint: null, cancellationToken);

    public static Task ExportConversationAsync(
        string sourcePath,
        string conversationId,
        string destinationPath,
        string? expectedRawFingerprint,
        CancellationToken cancellationToken)
        => ExportConversationsAsync(
            sourcePath,
            new[] { new RawJsonExportRequest(conversationId, destinationPath, expectedRawFingerprint) },
            cancellationToken);

    public static async Task ExportConversationsAsync(
        string sourcePath,
        IReadOnlyList<RawJsonExportRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
            return;

        var pending = new Dictionary<string, RawJsonExportRequest>(requests.Count, StringComparer.Ordinal);
        foreach (var request in requests)
        {
            if (!pending.TryAdd(request.ConversationId, request))
                throw new InvalidDataException($"Duplicate Complete JSON export request for conversation ID '{request.ConversationId}'.");
        }

        if (Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await using var file = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 100_000)
                throw new InvalidDataException("The ZIP contains an unexpectedly large number of entries.");

            var matches = archive.Entries.Where(static e => e.FullName.Equals("conversations.json", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("/conversations.json", StringComparison.OrdinalIgnoreCase)).ToArray();
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

            await using var stream = entry.Open();
            await ProcessConversationStreamAsync(stream, pending, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0)
                throw new InvalidDataException("Conversation JSON is empty.");
            if (stream.Length > MaxConversationJsonBytes)
                throw new InvalidDataException("Conversation JSON exceeds the supported safety limit.");

            await ProcessConversationStreamAsync(stream, pending, cancellationToken).ConfigureAwait(false);
        }

        if (pending.Count != 0)
        {
            throw new InvalidDataException(
                $"{pending.Count} selected original conversation record(s) could not be found in the source export. Missing IDs: "
                + string.Join(", ", pending.Keys.Take(5)));
        }
    }

    private static async Task ProcessConversationStreamAsync(
        Stream stream,
        IDictionary<string, RawJsonExportRequest> pending,
        CancellationToken cancellationToken)
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
            await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                replay,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (await TryExportSelectedAsync(item, pending, cancellationToken).ConfigureAwait(false)
                    && pending.Count == 0)
                    return;
            }
            return;
        }

        using var document = await JsonDocument.ParseAsync(replay, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var item in EnumerateDocument(document.RootElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryExportSelectedAsync(item, pending, cancellationToken).ConfigureAwait(false)
                && pending.Count == 0)
                return;
        }
    }

    private static async Task<bool> TryExportSelectedAsync(
        JsonElement item,
        IDictionary<string, RawJsonExportRequest> pending,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.ValueKind != JsonValueKind.Object)
            return false;

        var id = ChatExportReader.GetString(item, "id");
        if (id is null || !pending.TryGetValue(id, out var request))
            return false;

        await VerifyAndWriteAsync(item, request, cancellationToken).ConfigureAwait(false);
        pending.Remove(id);
        return true;
    }

    private static async Task VerifyAndWriteAsync(
        JsonElement conversation,
        RawJsonExportRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.ExpectedRawFingerprint))
        {
            var currentFingerprint = RawRecordFingerprint.Compute(conversation);
            if (!string.Equals(currentFingerprint, request.ExpectedRawFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Complete Conversation JSON export was refused because the original conversation record changed after import. Re-import the source before exporting.");
            }
        }

        await AtomicFile.WriteStreamAsync(
            request.DestinationPath,
            (file, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
                conversation.WriteTo(writer);
                writer.Flush();
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<JsonElement> EnumerateDocument(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikeConversation(root))
            {
                yield return root;
                yield break;
            }

            foreach (var wrapperName in new[] { "conversations", "items", "data" })
            {
                if (!root.TryGetProperty(wrapperName, out var wrapped) || wrapped.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in wrapped.EnumerateArray())
                    yield return item;
                yield break;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                yield return item;
        }
    }

    private static bool LooksLikeConversation(JsonElement element)
        => element.TryGetProperty("mapping", out _)
            || element.TryGetProperty("current_node", out _)
            || element.TryGetProperty("title", out _);

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
}

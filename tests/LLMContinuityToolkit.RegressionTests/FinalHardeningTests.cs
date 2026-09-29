using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LLMContinuityToolkit.Core;

internal static class FinalHardeningTests
{
    public static async Task RunAllAsync(CancellationToken cancellationToken)
    {
        TestWindowsFileNames();
        await TestDuplicateConversationIdsAsync(cancellationToken);
        await TestUnsupportedVisibleContentFailsClosedAsync(cancellationToken);
        await TestCompleteJsonRawMutationAsync(cancellationToken);
        await TestDuplicateConversationEntriesAsync(cancellationToken);
        await TestEmptyConversationEntryAsync(cancellationToken);
        await TestSuspiciousCompressionRatioAsync(cancellationToken);
        await TestTruncatedArchiveAsync(cancellationToken);
    }

    private static void TestWindowsFileNames()
    {
        Require(FileNameUtil.SafeFileName("CON").StartsWith("_CON", StringComparison.OrdinalIgnoreCase), "CON was not neutralized as a Windows device name.");
        Require(FileNameUtil.SafeFileName("nul.txt").StartsWith("_nul", StringComparison.OrdinalIgnoreCase), "NUL with extension was not neutralized.");
        Require(!FileNameUtil.SafeFileName("title.   ").EndsWith(".", StringComparison.Ordinal), "Trailing dot survived filename normalization.");
        Require(FileNameUtil.SafeFileName("Unicode ✓ 漢字 😀").Contains("漢字", StringComparison.Ordinal), "Valid Unicode was unnecessarily removed from a filename.");
        Require(FileNameUtil.SafeFileName(new string('x', 500)).Length <= 120, "Filename length limit was not enforced.");

        var root = Path.Combine(Path.GetTempPath(), "llm-continuity-filename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var desired = Path.Combine(root, "same.txt");
            File.WriteAllText(desired, "existing");
            var unique = FileNameUtil.UniquePath(desired);
            Require(unique.EndsWith("same (2).txt", StringComparison.OrdinalIgnoreCase), "Collision-safe filename suffix changed unexpectedly.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestDuplicateConversationIdsAsync(CancellationToken token)
    {
        var root = NewRoot("duplicate-id");
        try
        {
            var path = Path.Combine(root, "conversations.json");
            var records = new[]
            {
                BuildConversation("duplicate", "First", "alpha"),
                BuildConversation("duplicate", "Second", "beta")
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(records), new UTF8Encoding(false), token);
            await ExpectInvalidDataAsync(
                () => new ChatExportReader(new ActivitySink()).ReadMetadataAsync(path, cancellationToken: token),
                "Duplicate conversation IDs were accepted.",
                "Duplicate OpenAI account conversation ID");
        }
        finally { TryDelete(root); }
    }

    private static async Task TestUnsupportedVisibleContentFailsClosedAsync(CancellationToken token)
    {
        var root = NewRoot("unknown-visible");
        try
        {
            var path = Path.Combine(root, "conversations.json");
            var record = new
            {
                id = "future-content",
                title = "Future Content",
                create_time = 1_800_000_000,
                update_time = 1_800_000_010,
                current_node = "n1",
                mapping = new Dictionary<string, object?>
                {
                    ["n1"] = new
                    {
                        parent = (string?)null,
                        message = new
                        {
                            author = new { role = "assistant" },
                            create_time = 1_800_000_001,
                            recipient = "all",
                            channel = (string?)null,
                            metadata = new { is_visually_hidden_from_conversation = false, attachments = Array.Empty<object>() },
                            content = new { content_type = "future_canvas_v9", content = "future visible content" }
                        }
                    }
                }
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { record }), new UTF8Encoding(false), token);
            var imported = await new ChatExportReader(new ActivitySink()).ReadMetadataAsync(path, cancellationToken: token);
            var metadata = imported.Conversations.Single();
            Require(metadata.HasUnsupportedVisibleContent, "Unknown active visible content type was not marked fail-closed.");
            Require(metadata.UnsupportedVisibleContentTypes.Contains("future_canvas_v9", StringComparer.OrdinalIgnoreCase), "Unknown active content type name was not retained diagnostically.");

            var destination = Path.Combine(root, "out");
            await ExpectInvalidDataAsync(
                () => new ExportService(new ActivitySink()).ExportAsync(new[] { metadata }, ExportFormat.Markdown, destination, path, cancellationToken: token),
                "Readable export accepted unsupported active visible content.",
                "unsupported OpenAI account content type");

            var raw = await new ExportService(new ActivitySink()).ExportAsync(new[] { metadata }, ExportFormat.CompleteJson, destination, path, cancellationToken: token);
            Require(File.Exists(raw.OutputPath), "Complete JSON did not preserve an unsupported future raw record.");
        }
        finally { TryDelete(root); }
    }

    private static async Task TestCompleteJsonRawMutationAsync(CancellationToken token)
    {
        var root = NewRoot("raw-mutation");
        try
        {
            var path = Path.Combine(root, "conversations.json");
            var record = BuildConversation("raw-fingerprint", "Raw Fingerprint", "original text");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { record }), new UTF8Encoding(false), token);
            var imported = await new ChatExportReader(new ActivitySink()).ReadMetadataAsync(path, cancellationToken: token);
            var metadata = imported.Conversations.Single();
            Require(!string.IsNullOrWhiteSpace(metadata.RawRecordFingerprint), "Raw-record fingerprint was not retained in metadata.");

            var source = await File.ReadAllTextAsync(path, token);
            await File.WriteAllTextAsync(path, source.Replace("original text", "modified text", StringComparison.Ordinal), new UTF8Encoding(false), token);

            await ExpectInvalidDataAsync(
                () => new ExportService(new ActivitySink()).ExportAsync(new[] { metadata }, ExportFormat.CompleteJson, Path.Combine(root, "out"), path, cancellationToken: token),
                "Complete JSON export accepted a changed raw source record.",
                "changed after import");
        }
        finally { TryDelete(root); }
    }

    private static async Task TestDuplicateConversationEntriesAsync(CancellationToken token)
    {
        var root = NewRoot("duplicate-entry");
        try
        {
            var zip = Path.Combine(root, "duplicate.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                await WriteEntryAsync(archive, "conversations.json", "[]", token);
                await WriteEntryAsync(archive, "nested/conversations.json", "[]", token);
            }
            await ExpectInvalidDataAsync(
                () => new ChatExportReader(new ActivitySink()).ReadAsync(zip, cancellationToken: token),
                "ZIP with duplicate conversations.json entries was accepted.",
                "exactly one conversations.json");
        }
        finally { TryDelete(root); }
    }

    private static async Task TestEmptyConversationEntryAsync(CancellationToken token)
    {
        var root = NewRoot("empty-entry");
        try
        {
            var zip = Path.Combine(root, "empty.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                archive.CreateEntry("conversations.json", CompressionLevel.Optimal);
            await ExpectInvalidDataAsync(
                () => new ChatExportReader(new ActivitySink()).ReadAsync(zip, cancellationToken: token),
                "Zero-length conversations.json was accepted.",
                "empty");
        }
        finally { TryDelete(root); }
    }

    private static async Task TestSuspiciousCompressionRatioAsync(CancellationToken token)
    {
        var root = NewRoot("compression-ratio");
        try
        {
            var zip = Path.Combine(root, "ratio.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("conversations.json", CompressionLevel.SmallestSize);
                await using var stream = entry.Open();
                var block = Encoding.UTF8.GetBytes(new string(' ', 8192));
                for (var i = 0; i < 2200; i++)
                    await stream.WriteAsync(block, token);
            }
            await ExpectInvalidDataAsync(
                () => new ChatExportReader(new ActivitySink()).ReadAsync(zip, cancellationToken: token),
                "Pathological compression ratio was accepted.",
                "suspicious compression ratio");
        }
        finally { TryDelete(root); }
    }

    private static async Task TestTruncatedArchiveAsync(CancellationToken token)
    {
        var root = NewRoot("truncated-zip");
        try
        {
            var zip = Path.Combine(root, "truncated.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                await WriteEntryAsync(archive, "conversations.json", "[]", token);

            var bytes = await File.ReadAllBytesAsync(zip, token);
            await File.WriteAllBytesAsync(zip, bytes[..Math.Max(1, bytes.Length - 12)], token);
            await ExpectInvalidDataOrIoAsync(
                () => new ChatExportReader(new ActivitySink()).ReadAsync(zip, cancellationToken: token),
                "Truncated ZIP was accepted.");
        }
        finally { TryDelete(root); }
    }

    private static object BuildConversation(string id, string title, string text)
        => new
        {
            id,
            title,
            create_time = 1_800_000_000,
            update_time = 1_800_000_010,
            current_node = "n1",
            mapping = new Dictionary<string, object?>
            {
                ["n1"] = new
                {
                    parent = (string?)null,
                    message = new
                    {
                        author = new { role = "user" },
                        create_time = 1_800_000_001,
                        recipient = "all",
                        channel = (string?)null,
                        metadata = new { is_visually_hidden_from_conversation = false, attachments = Array.Empty<object>() },
                        content = new { content_type = "text", parts = new object[] { text } }
                    }
                }
            }
        };

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string content, CancellationToken token)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: false);
        await writer.WriteAsync(content.AsMemory(), token);
    }

    private static async Task ExpectInvalidDataAsync(Func<Task> action, string failure, string expectedFragment)
    {
        try
        {
            await action();
            throw new InvalidOperationException(failure);
        }
        catch (InvalidDataException ex)
        {
            Require(ex.Message.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase), $"Unexpected safety error: {ex.Message}");
        }
    }

    private static async Task ExpectInvalidDataOrIoAsync(Func<Task> action, string failure)
    {
        try
        {
            await action();
            throw new InvalidOperationException(failure);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Expected malformed-container failure.
        }
    }

    private static string NewRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"llm-continuity-final-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

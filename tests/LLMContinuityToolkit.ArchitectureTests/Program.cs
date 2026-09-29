using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMContinuityToolkit.Core;

var failures = new List<string>();
await RunAsync("streaming fingerprints preserve canonical fingerprint contract", TestFingerprintCompatibilityAsync);
await RunAsync("lean metadata index matches eager parser and hydrates exactly", TestMetadataParityAsync);
await RunAsync("single Complete JSON streams losslessly", TestSingleRawExportAsync);
await RunAsync("multi Complete JSON batches one verified source scan", TestBatchRawExportAsync);
await RunAsync("raw source mutation is rejected", TestRawMutationAsync);
await RunAsync("cancelled raw bundle leaves no destination output", TestRawCancellationAsync);

if (failures.Count != 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("ARCHITECTURE OPTIMIZATION REGRESSION FAILURES");
    foreach (var failure in failures)
        Console.Error.WriteLine(" - " + failure);
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine();
    Console.WriteLine("All architecture optimization regressions passed.");
}

async Task RunAsync(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL  {name}: {ex}");
    }
}

async Task TestFingerprintCompatibilityAsync()
{
    using var fixture = await ArchitectureFixture.CreateAsync();
    var store = new LazyConversationStore(new ActivitySink());
    var indexed = await store.ReadMetadataAsync(fixture.ZipPath);

    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.JsonPath));
    foreach (var element in document.RootElement.EnumerateArray())
    {
        var id = element.GetProperty("id").GetString() ?? throw new Exception("Fixture ID missing.");
        var record = indexed.Conversations.Single(row => row.Id == id);
        var oldCanonicalBytes = JsonSerializer.SerializeToUtf8Bytes(element);
        var expected = Convert.ToHexString(SHA256.HashData(oldCanonicalBytes)).ToLowerInvariant();
        Assert(record.RawRecordFingerprint == expected, $"Streaming raw fingerprint changed the canonical hash for {id}.");
        Assert(!string.IsNullOrWhiteSpace(record.TranscriptFingerprint), $"Transcript fingerprint missing for {id}.");
    }
}

async Task TestMetadataParityAsync()
{
    using var fixture = await ArchitectureFixture.CreateAsync();
    var activity = new ActivitySink();
    var eager = await new ChatExportReader(activity).ReadAsync(fixture.ZipPath);
    var store = new LazyConversationStore(activity);
    var indexed = await store.ReadMetadataAsync(fixture.ZipPath);

    Assert(indexed.Conversations.Count == eager.Conversations.Count, "Metadata index conversation count differs from eager reader.");
    foreach (var expected in eager.Conversations)
    {
        var metadata = indexed.Conversations.Single(row => row.Id == expected.Id);
        Assert(metadata.Messages.Count == 0, $"Metadata index retained visible message objects for {metadata.Id}.");
        Assert(metadata.MessageCount == expected.MessageCount, $"Message count mismatch for {metadata.Id}.");
        Assert(metadata.UserCount == expected.UserCount, $"User count mismatch for {metadata.Id}.");
        Assert(metadata.AssistantCount == expected.AssistantCount, $"Assistant count mismatch for {metadata.Id}.");
        Assert(metadata.AttachmentCount == expected.AttachmentCount, $"Attachment count mismatch for {metadata.Id}.");
        Assert(metadata.LastActiveMessageTimeRaw == expected.LastActiveMessageTimeRaw, $"Last-active timestamp mismatch for {metadata.Id}.");
        Assert(metadata.FinalHistoricalRole == expected.FinalHistoricalRole, $"Final role mismatch for {metadata.Id}.");
        Assert(metadata.UnsupportedVisibleContentTypes.SequenceEqual(expected.UnsupportedVisibleContentTypes, StringComparer.OrdinalIgnoreCase),
            $"Unsupported-content metadata mismatch for {metadata.Id}.");
    }

    var requested = indexed.Conversations.Reverse().ToArray();
    var hydrated = await store.HydrateSelectedAsync(fixture.ZipPath, requested);
    Assert(hydrated.Select(static row => row.Id).SequenceEqual(requested.Select(static row => row.Id)), "Hydration order changed.");
    foreach (var actual in hydrated)
    {
        var expected = eager.Conversations.Single(row => row.Id == actual.Id);
        Assert(actual.Messages.SequenceEqual(expected.Messages), $"Hydrated transcript differs message-for-message for {actual.Id}.");
    }
}

async Task TestSingleRawExportAsync()
{
    using var fixture = await ArchitectureFixture.CreateAsync();
    var activity = new ActivitySink();
    var indexed = await new LazyConversationStore(activity).ReadMetadataAsync(fixture.ZipPath);
    var row = indexed.Conversations.Single(record => record.Id == "conv-a");
    var folder = Path.Combine(fixture.Root, "single-raw");
    Directory.CreateDirectory(folder);

    var result = await new ExportService(activity).ExportAsync(new[] { row }, ExportFormat.CompleteJson, folder, fixture.ZipPath);
    Assert(File.Exists(result.OutputPath), "Single Complete JSON output was not finalized.");

    var outputNode = JsonNode.Parse(await File.ReadAllTextAsync(result.OutputPath)) ?? throw new Exception("Output JSON did not parse.");
    using var sourceDocument = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.JsonPath));
    var sourceElement = sourceDocument.RootElement.EnumerateArray().Single(element => element.GetProperty("id").GetString() == "conv-a");
    var sourceNode = JsonNode.Parse(sourceElement.GetRawText()) ?? throw new Exception("Source JSON did not parse.");
    Assert(JsonNode.DeepEquals(outputNode, sourceNode), "Streaming Complete JSON changed the selected raw record semantically.");
}

async Task TestBatchRawExportAsync()
{
    using var fixture = await ArchitectureFixture.CreateAsync();
    var activity = new ActivitySink();
    var events = new List<ActivityEvent>();
    activity.Activity += (_, item) => events.Add(item);
    var indexed = await new LazyConversationStore(activity).ReadMetadataAsync(fixture.ZipPath);
    var folder = Path.Combine(fixture.Root, "batch-raw");
    Directory.CreateDirectory(folder);

    var result = await new ExportService(activity).ExportAsync(indexed.Conversations, ExportFormat.CompleteJson, folder, fixture.ZipPath);
    Assert(result.IsBundle, "Two Complete JSON selections should produce one ZIP bundle.");
    Assert(result.VerifiedCount == 2, "Batch Complete JSON did not verify both selected records.");
    Assert(events.Count(item => item.Category == "RAW" && item.Message.Contains("one source scan", StringComparison.OrdinalIgnoreCase)) == 1,
        "Batch Complete JSON did not report its one-source-scan path exactly once.");

    using var archive = ZipFile.OpenRead(result.OutputPath);
    var jsonEntries = archive.Entries.Where(entry => entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && !entry.Name.Equals("bundle-manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
    Assert(jsonEntries.Length == 2, $"Expected two raw JSON payloads, got {jsonEntries.Length}.");
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in jsonEntries)
    {
        await using var stream = entry.Open();
        using var document = await JsonDocument.ParseAsync(stream);
        ids.Add(document.RootElement.GetProperty("id").GetString() ?? string.Empty);
    }
    Assert(ids.SetEquals(new[] { "conv-a", "conv-b" }), "Batch Complete JSON ZIP contains the wrong conversation records.");
    Assert(archive.GetEntry("bundle-manifest.json") is not null, "Batch Complete JSON manifest is missing.");
}

async Task TestRawMutationAsync()
{
    using var fixture = await ArchitectureFixture.CreateAsync();
    var activity = new ActivitySink();
    var store = new LazyConversationStore(activity);
    var indexed = await store.ReadMetadataAsync(fixture.JsonPath);
    var row = indexed.Conversations.Single(record => record.Id == "conv-a");
    var source = await File.ReadAllTextAsync(fixture.JsonPath);
    var mutated = source.Replace("Visible", "Changed", StringComparison.Ordinal);
    Assert(mutated != source, "Mutation fixture did not actually change serialized JSON content.");
    await File.WriteAllTextAsync(fixture.JsonPath, mutated, new UTF8Encoding(false));
    var folder = Path.Combine(fixture.Root, "mutated-raw");
    Directory.CreateDirectory(folder);

    try
    {
        await new ExportService(activity).ExportAsync(new[] { row }, ExportFormat.CompleteJson, folder, fixture.JsonPath);
        throw new Exception("Complete JSON accepted a source whose raw conversation record changed after indexing.");
    }
    catch (InvalidDataException ex)
    {
        Assert(ex.Message.Contains("changed after import", StringComparison.OrdinalIgnoreCase), "Raw mutation error did not explain the integrity failure.");
    }
    Assert(!Directory.EnumerateFiles(folder).Any(), "Failed raw mutation export left a finalized destination file.");
}

async Task TestRawCancellationAsync()
{
    using var fixture = await ArchitectureFixture.CreateAsync();
    var activity = new ActivitySink();
    var indexed = await new LazyConversationStore(activity).ReadMetadataAsync(fixture.ZipPath);
    var folder = Path.Combine(fixture.Root, "cancelled-raw");
    Directory.CreateDirectory(folder);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();

    try
    {
        await new ExportService(activity).ExportAsync(indexed.Conversations, ExportFormat.CompleteJson, folder, fixture.ZipPath, cancellationToken: cancelled.Token);
        throw new Exception("Pre-cancelled raw bundle unexpectedly completed.");
    }
    catch (OperationCanceledException)
    {
        // Expected.
    }
    Assert(!Directory.EnumerateFileSystemEntries(folder).Any(), "Cancelled raw bundle left output in the destination folder.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

sealed class ArchitectureFixture : IDisposable
{
    public required string Root { get; init; }
    public required string JsonPath { get; init; }
    public required string ZipPath { get; init; }

    public static async Task<ArchitectureFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "llm-continuity-architecture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var jsonPath = Path.Combine(root, "conversations.json");
        var payload = new[]
        {
            BuildConversation("conv-a", "Same Title", 1_780_000_000, "Visible α answer"),
            BuildConversation("conv-b", "Same Title", 1_780_000_000, "Visible β answer")
        };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(payload), new UTF8Encoding(false));
        var zipPath = Path.Combine(root, "export.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(jsonPath, "conversations.json", CompressionLevel.Optimal);
        return new ArchitectureFixture { Root = root, JsonPath = jsonPath, ZipPath = zipPath };
    }

    private static object BuildConversation(string id, string title, long timestamp, string answer)
    {
        var mapping = new Dictionary<string, object?>
        {
            ["n1"] = Node(null, Message("user", "Hello\nworld", timestamp + 1)),
            ["n2"] = Node("n1", Message("assistant", answer, timestamp + 2)),
            ["n3"] = Node("n2", Message("user", new object[]
            {
                "Attachment turn",
                new { content_type = "image_asset_pointer", name = "diagram.png", asset_pointer = "file-service://fixture" }
            }, timestamp + 3)),
            ["n4"] = Node("n3", Message("assistant", "Final answer", timestamp + 4))
        };

        return new
        {
            id,
            title,
            create_time = timestamp,
            update_time = timestamp + 10,
            current_node = "n4",
            mapping,
            fixture_unicode = "é — 雪",
            fixture_number = 1.2300
        };
    }

    private static object Node(string? parent, object message) => new { parent, message };

    private static object Message(string role, object content, long timestamp)
    {
        var parts = content is object[] array ? array : new object[] { content };
        return new
        {
            author = new { role },
            create_time = timestamp,
            recipient = "all",
            channel = (string?)null,
            metadata = new { is_visually_hidden_from_conversation = false, attachments = Array.Empty<object>() },
            content = new { content_type = "multimodal_text", parts }
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }
}

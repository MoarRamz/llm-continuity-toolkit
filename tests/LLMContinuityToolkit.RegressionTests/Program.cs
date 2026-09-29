using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LLMContinuityToolkit.Core;

var failures = new List<string>();
await RunAsync("active path, visibility filters, attachments, reasoning recaps", TestParserAsync);
await RunAsync("metadata-only index, fingerprint, cancellation, and hydration parity", TestLazyStoreAsync);
await RunAsync("continuation verification and bundle integrity", TestBundleAsync);
await RunAsync("complete JSON selected-record export", TestCompleteJsonAsync);
await RunAsync("golden output, corruption checks, cancellation cleanup, randomized hardening", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    await HardeningTests.RunAllAsync(timeout.Token);
});
await RunAsync("final source-integrity, hostile ZIP, future-schema, and filename hardening", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    await FinalHardeningTests.RunAllAsync(timeout.Token);
});

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("REGRESSION FAILURES");
    foreach (var failure in failures) Console.Error.WriteLine(" - " + failure);
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine();
    Console.WriteLine("All synthetic regression tests passed.");
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

async Task TestParserAsync()
{
    using var fixture = await SyntheticFixture.CreateAsync();
    var activity = new ActivitySink();
    var reader = new ChatExportReader(activity);
    var result = await reader.ReadAsync(fixture.ZipPath);
    Assert(result.Conversations.Count == 2, "Expected two conversations.");

    var first = result.Conversations.Single(c => c.Id == "conv-1");
    Assert(first.MessageCount == 7, $"Expected 7 visible active-path messages, got {first.MessageCount}.");
    Assert(first.UserCount == 4, $"Expected 4 user messages, got {first.UserCount}.");
    Assert(first.AssistantCount == 3, $"Expected 3 assistant messages, got {first.AssistantCount}.");
    Assert(first.Messages.All(m => !m.Text.Contains("ABANDONED", StringComparison.Ordinal)), "Abandoned branch leaked into visible transcript.");
    Assert(first.Messages.All(m => !m.Text.Contains("HIDDEN", StringComparison.Ordinal)), "Hidden message leaked into visible transcript.");
    Assert(first.Messages.All(m => !m.Text.Contains("TOOL", StringComparison.Ordinal)), "Tool-directed message leaked into visible transcript.");
    Assert(first.Messages.All(m => !m.Text.Contains("SECRET REASONING", StringComparison.Ordinal)), "Analysis message leaked into visible transcript.");
    Assert(first.Messages.All(m => !m.Text.StartsWith("Worked for ", StringComparison.OrdinalIgnoreCase)), "Reasoning recap leaked into visible transcript.");
    Assert(first.Messages.All(m => !m.Text.StartsWith("Thought for ", StringComparison.OrdinalIgnoreCase)), "Thinking duration leaked into visible transcript.");
    Assert(first.Messages.Any(m => m.Text.Contains("[Uploaded image: diagram.png]", StringComparison.Ordinal)), "Named image marker was not preserved.");
    Assert(result.Compatibility.SkippedInvisibleMessages == 2, "Hidden compatibility counter mismatch.");
    Assert(result.Compatibility.SkippedToolDirectedMessages == 2, "Tool-directed compatibility counter mismatch.");
    Assert(result.Compatibility.SkippedAnalysisMessages == 2, "Analysis compatibility counter mismatch.");
    Assert(result.Compatibility.SkippedReasoningRecaps == 6, "Reasoning-recap compatibility counter mismatch.");
}

async Task TestLazyStoreAsync()
{
    using var fixture = await SyntheticFixture.CreateAsync();
    var activity = new ActivitySink();
    var eager = await new ChatExportReader(activity).ReadAsync(fixture.ZipPath);
    var beforeTemps = Directory.EnumerateFiles(Path.GetTempPath(), "llm-continuity-hydrate-*.json")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var store = new LazyConversationStore(activity);
    var indexed = await store.ReadMetadataAsync(fixture.ZipPath);
    Assert(indexed.Conversations.Count == eager.Conversations.Count, "Lazy index conversation count mismatch.");

    foreach (var eagerRecord in eager.Conversations)
    {
        var metadata = indexed.Conversations.Single(record => record.Id == eagerRecord.Id);
        Assert(metadata.Messages.Count == 0, $"Metadata index retained transcript text for {metadata.Id}.");
        Assert(!metadata.HasTranscript, $"Metadata index incorrectly reports a hydrated transcript for {metadata.Id}.");
        Assert(metadata.MessageCount == eagerRecord.MessageCount, $"Metadata message count mismatch for {metadata.Id}.");
        Assert(metadata.UserCount == eagerRecord.UserCount, $"Metadata user count mismatch for {metadata.Id}.");
        Assert(metadata.AssistantCount == eagerRecord.AssistantCount, $"Metadata assistant count mismatch for {metadata.Id}.");
        Assert(metadata.AttachmentCount == eagerRecord.AttachmentCount, $"Metadata attachment count mismatch for {metadata.Id}.");
        Assert(metadata.LastActiveMessageTimeRaw == eagerRecord.LastActiveMessageTimeRaw, $"Metadata last-active timestamp mismatch for {metadata.Id}.");
        Assert(metadata.FinalHistoricalRole == eagerRecord.FinalHistoricalRole, $"Metadata final role mismatch for {metadata.Id}.");
        Assert(!string.IsNullOrWhiteSpace(metadata.TranscriptFingerprint), $"Metadata transcript fingerprint missing for {metadata.Id}.");
        Assert(!string.IsNullOrWhiteSpace(metadata.RawRecordFingerprint), $"Metadata raw-record fingerprint missing for {metadata.Id}.");
    }

    var requested = indexed.Conversations.Reverse().ToArray();
    var hydrated = await store.HydrateSelectedAsync(fixture.ZipPath, requested);
    Assert(hydrated.Count == requested.Length, "Lazy hydration count mismatch.");
    Assert(hydrated.Select(static record => record.Id).SequenceEqual(requested.Select(static record => record.Id)), "Lazy hydration did not preserve selection order.");

    foreach (var hydratedRecord in hydrated)
    {
        var eagerRecord = eager.Conversations.Single(record => record.Id == hydratedRecord.Id);
        Assert(hydratedRecord.HasTranscript, $"Hydrated record {hydratedRecord.Id} does not report a transcript.");
        Assert(hydratedRecord.MessageCount == eagerRecord.MessageCount, $"Hydrated message count mismatch for {hydratedRecord.Id}.");
        Assert(hydratedRecord.Messages.SequenceEqual(eagerRecord.Messages), $"Hydrated transcript differs from eager transcript for {hydratedRecord.Id}.");
    }

    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            await store.HydrateSelectedAsync(fixture.ZipPath, new[] { indexed.Conversations[0] }, cancelled.Token);
            throw new Exception("Cancelled lazy hydration unexpectedly completed.");
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    var directIndexed = await store.ReadMetadataAsync(fixture.JsonPath);
    var directMetadata = directIndexed.Conversations.Single(record => record.Id == "conv-1");
    var originalJson = await File.ReadAllTextAsync(fixture.JsonPath);
    Assert(originalJson.Contains("Visible answer", StringComparison.Ordinal), "Mutation fixture text was not found.");
    await File.WriteAllTextAsync(
        fixture.JsonPath,
        originalJson.Replace("Visible answer", "Changed answer", StringComparison.Ordinal),
        new UTF8Encoding(false));

    try
    {
        await store.HydrateSelectedAsync(fixture.JsonPath, new[] { directMetadata });
        throw new Exception("Hydration accepted a source whose visible transcript changed after indexing.");
    }
    catch (InvalidDataException ex)
    {
        Assert(ex.Message.Contains("may have changed", StringComparison.OrdinalIgnoreCase), "Source-mutation failure did not explain the integrity mismatch.");
    }

    var afterTemps = Directory.EnumerateFiles(Path.GetTempPath(), "llm-continuity-hydrate-*.json")
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    afterTemps.ExceptWith(beforeTemps);
    Assert(afterTemps.Count == 0, "Lazy hydration left a private staging file behind.");
}

async Task TestBundleAsync()
{
    using var fixture = await SyntheticFixture.CreateAsync();
    var activity = new ActivitySink();
    var reader = new ChatExportReader(activity);
    var imported = await reader.ReadAsync(fixture.ZipPath);
    var folder = Path.Combine(fixture.Root, "exports");
    Directory.CreateDirectory(folder);

    var exporter = new ExportService(activity);
    var result = await exporter.ExportAsync(imported.Conversations, ExportFormat.ContinuationMarkdown, folder, fixture.ZipPath);
    Assert(result.IsBundle, "Multi-conversation continuation export should be a ZIP bundle.");
    Assert(result.VerifiedCount == 2, "Both continuation files should verify.");
    var prompt = result.ContinuationPrompt ?? throw new Exception("Bundle continuation prompt was null.");
    Assert(prompt == ContinuationInstructions.ForBundle(2), "Bundle prompt mismatch.");
    Assert(prompt.Contains("2 Continuation Markdown files", StringComparison.Ordinal), "Bundle prompt did not include the exact file count and current format name.");
    Assert(File.Exists(result.OutputPath), "Bundle ZIP was not created.");

    using var archive = ZipFile.OpenRead(result.OutputPath);
    var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
    Assert(names.Contains("00 - READ ME FIRST - Continuation Instructions.txt"), "Bundle instructions missing.");
    Assert(names.Contains("bundle-manifest.json"), "Bundle manifest missing.");
    Assert(archive.Entries.Count == 4, $"Expected 4 ZIP entries, got {archive.Entries.Count}.");

    var instructionsEntry = archive.GetEntry("00 - READ ME FIRST - Continuation Instructions.txt") ?? throw new Exception("Instructions entry not found.");
    using (var instructionsReader = new StreamReader(instructionsEntry.Open(), Encoding.UTF8))
    {
        var instructions = await instructionsReader.ReadToEndAsync();
        Assert(instructions.Contains(prompt, StringComparison.Ordinal), "Embedded instructions and copied prompt diverged.");
        Assert(instructions.Contains("Continuation files in this archive: 2", StringComparison.Ordinal), "Embedded instruction count mismatch.");
    }

    var manifestEntry = archive.GetEntry("bundle-manifest.json") ?? throw new Exception("Manifest entry not found.");
    using var readerStream = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
    var manifestJson = await readerStream.ReadToEndAsync();
    var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestJson) ?? throw new Exception("Manifest could not be parsed.");
    Assert(manifest.ConversationCount == 2, "Manifest conversation count mismatch.");
    Assert(manifest.Files.Count == 2, "Manifest file count mismatch.");
    Assert(manifest.Developer == "DevMoarRamz", "Manifest developer metadata mismatch.");
    Assert(manifest.PayloadHashAlgorithm == "SHA-256", "Manifest hash algorithm metadata mismatch.");
}

async Task TestCompleteJsonAsync()
{
    using var fixture = await SyntheticFixture.CreateAsync();
    var activity = new ActivitySink();
    var reader = new ChatExportReader(activity);
    var imported = await reader.ReadMetadataAsync(fixture.ZipPath);
    var row = imported.Conversations.Single(c => c.Id == "conv-1");
    Assert(!string.IsNullOrWhiteSpace(row.RawRecordFingerprint), "Complete JSON fixture did not retain a raw-record fingerprint.");
    var folder = Path.Combine(fixture.Root, "json-export");
    Directory.CreateDirectory(folder);
    var exporter = new ExportService(activity);
    var result = await exporter.ExportAsync(new[] { row }, ExportFormat.CompleteJson, folder, fixture.ZipPath);
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(result.OutputPath));
    Assert(document.RootElement.GetProperty("id").GetString() == "conv-1", "Complete JSON exported wrong record.");
    Assert(document.RootElement.TryGetProperty("mapping", out _), "Complete JSON lost mapping data.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class SyntheticFixture : IDisposable
{
    public required string Root { get; init; }
    public required string JsonPath { get; init; }
    public required string ZipPath { get; init; }

    public static async Task<SyntheticFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "llm-continuity-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var jsonPath = Path.Combine(root, "conversations.json");
        var conversations = new[]
        {
            BuildConversation("conv-1", "Synthetic Main", 1_760_000_000),
            BuildConversation("conv-2", "Synthetic Main", 1_760_100_000)
        };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(conversations), new UTF8Encoding(false));
        var zipPath = Path.Combine(root, "synthetic-export.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(jsonPath, "conversations.json", CompressionLevel.Optimal);
        return new SyntheticFixture { Root = root, JsonPath = jsonPath, ZipPath = zipPath };
    }

    private static object BuildConversation(string id, string title, long timestamp)
    {
        var mapping = new Dictionary<string, object?>
        {
            ["n1"] = Node(null, Message("user", "Hello", timestamp + 1)),
            ["n2"] = Node("n1", Message("assistant", "Visible answer", timestamp + 2)),
            ["n3"] = Node("n2", Message("user", new object[] { "Here is an image", new { content_type = "image_asset_pointer", name = "diagram.png", asset_pointer = "file-service://abc" } }, timestamp + 3)),
            ["n4"] = Node("n3", Message("assistant", "ABANDONED RESPONSE", timestamp + 4)),
            ["n5"] = Node("n3", Message("assistant", "Active regenerated response", timestamp + 5)),
            ["n6"] = Node("n5", Message("assistant", "HIDDEN IMPLEMENTATION", timestamp + 6, hidden: true)),
            ["n7"] = Node("n6", Message("user", "Continue", timestamp + 7)),
            ["n8"] = Node("n7", Message("assistant", "TOOL OUTPUT", timestamp + 8, recipient: "python")),
            ["n9"] = Node("n8", Message("assistant", "Visible after tool", timestamp + 9)),
            ["n10"] = Node("n9", Message("assistant", "SECRET REASONING", timestamp + 10, channel: "analysis")),
            ["n11"] = Node("n10", ReasoningRecap("Worked for 9m 41s", timestamp + 11)),
            ["n12"] = Node("n11", ReasoningRecap("Thought for 4 seconds", timestamp + 12)),
            ["n13"] = Node("n12", ReasoningRecap("Thought for a couple of seconds", timestamp + 13)),
            ["n14"] = Node("n13", Message("user", "Final user turn", timestamp + 14))
        };

        return new
        {
            id,
            title,
            create_time = timestamp,
            update_time = timestamp + 20,
            current_node = "n14",
            mapping
        };
    }

    private static object Node(string? parent, object message) => new { parent, message };

    private static object Message(string role, object contentValue, long time, bool hidden = false, string recipient = "all", string? channel = null)
    {
        var parts = contentValue is object[] array ? array : new object[] { contentValue };
        return new
        {
            author = new { role },
            create_time = time,
            recipient,
            channel,
            metadata = new { is_visually_hidden_from_conversation = hidden, attachments = Array.Empty<object>() },
            content = new { content_type = "multimodal_text", parts }
        };
    }

    private static object ReasoningRecap(string text, long time)
        => new
        {
            author = new { role = "assistant" },
            create_time = time,
            recipient = (string?)null,
            channel = (string?)null,
            metadata = new { model_slug = "llm-5-6-thinking", attachments = Array.Empty<object>() },
            content = new { content_type = "reasoning_recap", content = text }
        };

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

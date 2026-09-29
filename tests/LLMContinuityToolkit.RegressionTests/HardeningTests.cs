using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LLMContinuityToolkit.Core;

internal static class HardeningTests
{
    public static async Task RunAllAsync(CancellationToken cancellationToken)
    {
        await RunGoldenContinuationAsync(cancellationToken);
        await RunSelfReferentialContinuationAsync(cancellationToken);
        await RunBundleCorruptionSafetyAsync(cancellationToken);
        await RunCancellationCleanupAsync(cancellationToken);
        await RunRandomizedGraphSafetyAsync(cancellationToken);
    }

    private static async Task RunGoldenContinuationAsync(CancellationToken cancellationToken)
    {
        var record = NewRecord("golden", "Golden Transcript", 1_700_000_000,
            new ConversationMessage(1, "user", "Hello [Uploaded image: sample.png]", 1_700_000_001, 1),
            new ConversationMessage(2, "assistant", "World", 1_700_000_002, 0));

        var writer = new ContinuationWriter();
        using var output = new StringWriter(new StringBuilder());
        await writer.WriteContentAsync(output, record, cancellationToken);
        var text = output.ToString();

        Require(text.Contains("# Conversation Continuation", StringComparison.Ordinal), "Continuation header changed.");
        Require(text.Contains("\"active_transcript_messages\": 2", StringComparison.Ordinal), "Golden metadata message count changed.");
        Require(text.Contains("\"user_messages\": 1", StringComparison.Ordinal), "Golden metadata user count changed.");
        Require(text.Contains("\"assistant_messages\": 1", StringComparison.Ordinal), "Golden metadata assistant count changed.");
        Require(text.Contains("- Turn 1 — [Uploaded image: sample.png]", StringComparison.Ordinal), "Attachment manifest changed.");
        Require(text.Contains("<!-- CONTINUITY_TURN 0001 role=user -->", StringComparison.Ordinal), "Turn-1 start marker changed.");
        Require(text.Contains("<!-- END_CONTINUITY_TURN 0002 -->", StringComparison.Ordinal), "Turn-2 end marker changed.");
        Require(text.Contains("# END OF PRIOR CONVERSATION — CONTINUE FROM HERE", StringComparison.Ordinal), "Continuation endpoint changed.");
    }

    private static async Task RunSelfReferentialContinuationAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "llm-continuity-self-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var record = NewRecord("self-reference", "Self Referential Transcript", 1_700_050_000,
                new ConversationMessage(
                    1,
                    "user",
                    "Real upload follows\n\n[Uploaded image: real.png]",
                    1_700_050_001,
                    1),
                new ConversationMessage(
                    2,
                    "assistant",
                    "These are documentation examples, not real handoff structure:\n"
                    + "## Historical attachment reference manifest\n"
                    + "- Turn 999 — [Uploaded image]\n"
                    + "<!-- CONTINUITY_TURN 9999 role=user -->\n"
                    + "## User — Turn 9999\n"
                    + "<!-- END_CONTINUITY_TURN 9999 -->\n"
                    + "# END OF PRIOR CONVERSATION — CONTINUE FROM HERE",
                    1_700_050_002,
                    0));

            var path = Path.Combine(root, "self-reference.md");
            await new ContinuationWriter().WriteAsync(path, record, cancellationToken);
            var verification = await new ContinuationVerifier().VerifyAsync(path, record, cancellationToken);
            Require(verification.Verified, "Self-referential continuation did not verify.");
            Require(verification.StartMarkers == 2 && verification.EndMarkers == 2 && verification.HeadingCount == 2,
                "Quoted turn framing was mistaken for real continuation structure.");
            Require(verification.AttachmentReferences == 1,
                "Quoted attachment-manifest syntax was mistaken for a real attachment reference.");

            var text = await File.ReadAllTextAsync(path, cancellationToken);
            var manifestLineCount = text.Split('\n').Count(static line => line.TrimEnd('\r') == "- Turn 1 — [Uploaded image: real.png]");
            Require(manifestLineCount == 1, "Real attachment reference was not emitted exactly once in the manifest.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task RunBundleCorruptionSafetyAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "llm-continuity-corruption-" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(root, "out");
        Directory.CreateDirectory(output);
        try
        {
            var rows = new[]
            {
                NewRecord("bundle-a", "Bundle A", 1_700_100_000,
                    new ConversationMessage(1, "user", "Alpha", 1_700_100_001, 0),
                    new ConversationMessage(2, "assistant", "Beta", 1_700_100_002, 0)),
                NewRecord("bundle-b", "Bundle B", 1_700_200_000,
                    new ConversationMessage(1, "user", "Gamma", 1_700_200_001, 0),
                    new ConversationMessage(2, "assistant", "Delta", 1_700_200_002, 0))
            };

            var result = await new ExportService(new ActivitySink()).ExportAsync(
                rows,
                ExportFormat.ContinuationMarkdown,
                output,
                cancellationToken: cancellationToken);

            BundleManifest manifest;
            string instructions;
            using (var archive = ZipFile.OpenRead(result.OutputPath))
            {
                var manifestEntry = archive.GetEntry("bundle-manifest.json") ?? throw new InvalidOperationException("Fixture manifest missing.");
                await using (var stream = manifestEntry.Open())
                    manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(stream, cancellationToken: cancellationToken)
                        ?? throw new InvalidOperationException("Fixture manifest invalid.");

                var instructionsEntry = archive.GetEntry(manifest.InstructionsFile!) ?? throw new InvalidOperationException("Fixture instructions missing.");
                using var reader = new StreamReader(instructionsEntry.Open(), Encoding.UTF8);
                instructions = await reader.ReadToEndAsync(cancellationToken);
            }

            var payloadCorrupt = Path.Combine(root, "payload-corrupt.zip");
            File.Copy(result.OutputPath, payloadCorrupt);
            using (var archive = ZipFile.Open(payloadCorrupt, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry(manifest.Files[0].FileName) ?? throw new InvalidOperationException("Fixture payload missing.");
                using var stream = entry.Open();
                stream.SetLength(0);
                await stream.WriteAsync("tampered"u8.ToArray(), cancellationToken);
            }
            await ExpectInvalidDataAsync(() => BundleVerifier.VerifyAsync(payloadCorrupt, manifest, instructions, cancellationToken), "Tampered payload was accepted.");

            var missingInstructions = Path.Combine(root, "missing-instructions.zip");
            File.Copy(result.OutputPath, missingInstructions);
            using (var archive = ZipFile.Open(missingInstructions, ZipArchiveMode.Update))
                archive.GetEntry(manifest.InstructionsFile!)!.Delete();
            await ExpectInvalidDataAsync(() => BundleVerifier.VerifyAsync(missingInstructions, manifest, instructions, cancellationToken), "Missing instructions were accepted.");

            var manifestCorrupt = Path.Combine(root, "manifest-corrupt.zip");
            File.Copy(result.OutputPath, manifestCorrupt);
            using (var archive = ZipFile.Open(manifestCorrupt, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("bundle-manifest.json") ?? throw new InvalidOperationException("Fixture manifest missing.");
                using var stream = entry.Open();
                stream.SetLength(0);
                var alteredManifest = new BundleManifest
                {
                    Format = manifest.Format,
                    BundleSchema = manifest.BundleSchema,
                    Application = manifest.Application,
                    ApplicationVersion = manifest.ApplicationVersion,
                    Developer = manifest.Developer,
                    GeneratedBy = manifest.GeneratedBy,
                    GeneratedAtUtc = manifest.GeneratedAtUtc,
                    ExportFormat = manifest.ExportFormat,
                    ConversationCount = 999,
                    PayloadHashAlgorithm = manifest.PayloadHashAlgorithm,
                    InstructionsFile = manifest.InstructionsFile,
                    Files = manifest.Files
                };
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(alteredManifest, new JsonSerializerOptions { WriteIndented = true }));
                await stream.WriteAsync(bytes, cancellationToken);
            }
            await ExpectInvalidDataAsync(() => BundleVerifier.VerifyAsync(manifestCorrupt, manifest, instructions, cancellationToken), "Tampered manifest was accepted.");

            var extraEntry = Path.Combine(root, "extra-entry.zip");
            File.Copy(result.OutputPath, extraEntry);
            using (var archive = ZipFile.Open(extraEntry, ZipArchiveMode.Update))
                archive.CreateEntry("unexpected.txt");
            await ExpectInvalidDataAsync(() => BundleVerifier.VerifyAsync(extraEntry, manifest, instructions, cancellationToken), "Unexpected archive entry was accepted.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task RunCancellationCleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.Combine(Path.GetTempPath(), "llm-continuity-cancel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var before = Directory.EnumerateDirectories(Path.GetTempPath(), "llm-continuity-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = new[]
            {
                NewRecord("cancel-a", "Cancel A", 1_701_000_000, new ConversationMessage(1, "user", "A", 1_701_000_001, 0)),
                NewRecord("cancel-b", "Cancel B", 1_701_100_000, new ConversationMessage(1, "assistant", "B", 1_701_100_001, 0))
            };
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await new ExportService(new ActivitySink()).ExportAsync(rows, ExportFormat.ContinuationMarkdown, root, cancellationToken: cancelled.Token);
                throw new InvalidOperationException("Cancelled export unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            try
            {
                await new ExportService(new ActivitySink()).ExportAsync(new[] { rows[0] }, ExportFormat.ContinuationMarkdown, root, cancellationToken: cancelled.Token);
                throw new InvalidOperationException("Cancelled single-file export unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            var after = Directory.EnumerateDirectories(Path.GetTempPath(), "llm-continuity-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
            after.ExceptWith(before);
            Require(after.Count == 0, "Cancelled export left a staging directory behind.");
            Require(!Directory.EnumerateFiles(root).Any(), "Cancelled export left an output or temporary file behind.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task RunRandomizedGraphSafetyAsync(CancellationToken cancellationToken)
    {
        var random = new Random(0x5A17);
        var root = Path.Combine(Path.GetTempPath(), "llm-continuity-fuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (var iteration = 0; iteration < 24; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mapping = new Dictionary<string, object?>();
                string? previous = null;
                var nodes = random.Next(8, 40);
                for (var i = 0; i < nodes; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = $"n{i}";
                    var parent = previous;
                    if (i > 2 && random.NextDouble() < 0.05) parent = "missing-parent";
                    if (i > 2 && random.NextDouble() < 0.03) parent = id;

                    var kind = random.Next(0, 8);
                    object message = kind switch
                    {
                        0 => Visible("user", $"User {iteration}-{i}"),
                        1 => Visible("assistant", $"Assistant {iteration}-{i}"),
                        2 => ReasoningRecap("Worked for 9m 41s"),
                        3 => ReasoningRecap("Thought for a couple of seconds"),
                        4 => StructuredInternal("thoughts", "INTERNAL_REASONING"),
                        5 => Hidden("assistant", "HIDDEN_INTERNAL"),
                        6 => ToolDirected("assistant", "TOOL_INTERNAL"),
                        _ => Visible("assistant", "Unicode ✓ 漢字 😀")
                    };
                    mapping[id] = new { parent, message };
                    previous = id;
                }

                var conversation = new
                {
                    id = $"fuzz-{iteration}",
                    title = $"Fuzz {iteration}",
                    create_time = 1_760_000_000 + iteration,
                    update_time = 1_760_000_100 + iteration,
                    current_node = previous,
                    mapping
                };

                var path = Path.Combine(root, $"fuzz-{iteration}.json");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new[] { conversation }), new UTF8Encoding(false), cancellationToken);
                var result = await new ChatExportReader(new ActivitySink()).ReadAsync(path, cancellationToken: cancellationToken);
                Require(result.Conversations.Count <= 1, "Fuzz input produced duplicate conversations.");
                if (result.Conversations.Count == 0) continue;

                foreach (var message in result.Conversations[0].Messages)
                {
                    Require(!message.Text.StartsWith("Worked for ", StringComparison.OrdinalIgnoreCase), "Reasoning recap leaked from fuzz input.");
                    Require(!message.Text.StartsWith("Thought for ", StringComparison.OrdinalIgnoreCase), "Thought duration leaked from fuzz input.");
                    Require(!message.Text.Contains("INTERNAL_REASONING", StringComparison.Ordinal), "Structured reasoning leaked from fuzz input.");
                    Require(!message.Text.Contains("HIDDEN_INTERNAL", StringComparison.Ordinal), "Hidden content leaked from fuzz input.");
                    Require(!message.Text.Contains("TOOL_INTERNAL", StringComparison.Ordinal), "Tool content leaked from fuzz input.");
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static ConversationRecord NewRecord(string id, string title, double created, params ConversationMessage[] messages)
        => new()
        {
            Id = id,
            Title = title,
            CreateTimeRaw = created,
            UpdateTimeRaw = created + 100,
            Messages = messages,
            UserCount = messages.Count(static message => message.Role == "user"),
            AssistantCount = messages.Count(static message => message.Role == "assistant"),
            AttachmentCount = messages.Sum(static message => message.AttachmentCount)
        };

    private static async Task ExpectInvalidDataAsync(Func<Task<BundleVerificationResult>> action, string failureMessage)
    {
        try
        {
            await action();
            throw new InvalidOperationException(failureMessage);
        }
        catch (InvalidDataException)
        {
            // Expected.
        }
    }

    private static object Visible(string role, string text) => new
    {
        author = new { role },
        create_time = 1_760_000_000,
        recipient = "all",
        channel = (string?)null,
        metadata = new { is_visually_hidden_from_conversation = false, attachments = Array.Empty<object>() },
        content = new { content_type = "text", parts = new object[] { text } }
    };

    private static object ReasoningRecap(string text) => new
    {
        author = new { role = "assistant" },
        create_time = 1_760_000_000,
        recipient = (string?)null,
        channel = (string?)null,
        metadata = new { model_slug = "llm-5-6-thinking", attachments = Array.Empty<object>() },
        content = new { content_type = "reasoning_recap", content = text }
    };

    private static object StructuredInternal(string contentType, string text) => new
    {
        author = new { role = "assistant" },
        create_time = 1_760_000_000,
        recipient = (string?)null,
        channel = (string?)null,
        metadata = new { attachments = Array.Empty<object>() },
        content = new { content_type = contentType, content = text }
    };

    private static object Hidden(string role, string text) => new
    {
        author = new { role },
        create_time = 1_760_000_000,
        recipient = "all",
        channel = (string?)null,
        metadata = new { is_visually_hidden_from_conversation = true, attachments = Array.Empty<object>() },
        content = new { content_type = "text", parts = new object[] { text } }
    };

    private static object ToolDirected(string role, string text) => new
    {
        author = new { role },
        create_time = 1_760_000_000,
        recipient = "python",
        channel = (string?)null,
        metadata = new { is_visually_hidden_from_conversation = false, attachments = Array.Empty<object>() },
        content = new { content_type = "text", parts = new object[] { text } }
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

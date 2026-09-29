using System.Runtime.CompilerServices;
using LLMContinuityToolkit.Core;

internal static class ReleaseRegressionGuards
{
    [ModuleInitializer]
    internal static void Run()
    {
        RunFailedImportRedactionRegression();
        RunRenamedStagingCancellationRegression();
        VerifyCiRuntimeLegalAssets();
    }

    private static void RunFailedImportRedactionRegression()
    {
        const string title = "Synthetic Private Conversation";
        const string id = "synthetic-private-conversation-id";
        const string sourcePath = @"C:\Users\SyntheticUser\Downloads\synthetic-chat-export.zip";
        const string sourceFile = "synthetic-chat-export.zip";

        var sink = new ActivitySink();
        var history = new List<ActivityEvent>();
        sink.Activity += (_, entry) => history.Add(entry);

        sink.RegisterPath(sourcePath);
        sink.RegisterPath(sourceFile);

        // Simulate a partially indexed import that later fails and therefore never populates UI rows.
        // ActivitySink.Write must register sensitivity before it publishes each event.
        sink.Write("IMPORT", $"Opening OpenAI account export: {sourceFile}");
        sink.Write("INDEX", $"1  {title} — 42 visible messages");
        sink.Write("COMPAT", $"Duplicate OpenAI account conversation ID '{id}' was found. Import stopped because later lazy hydration would be ambiguous.", ActivityLevel.Error);
        sink.Write("PERF", "Transcript indexing completed in 1.23 s", ActivityLevel.Performance);

        var sensitive = sink.GetSensitiveSnapshot();
        var redacted = string.Join(Environment.NewLine, history.Select(entry => DiagnosticRedactor.Redact(entry.Display, sensitive)));

        Require(!redacted.Contains(title, StringComparison.Ordinal), "Failed-import redaction leaked a conversation title.");
        Require(!redacted.Contains(id, StringComparison.Ordinal), "Failed-import redaction leaked a stable conversation identifier.");
        Require(!redacted.Contains(sourcePath, StringComparison.OrdinalIgnoreCase), "Failed-import redaction leaked an absolute source path.");
        Require(!redacted.Contains(sourceFile, StringComparison.OrdinalIgnoreCase), "Failed-import redaction leaked the source filename.");
        Require(redacted.Contains("Conversation 001", StringComparison.Ordinal), "Failed-import redaction did not preserve a conversation pseudonym.");
        Require(redacted.Contains("<conversation-id>", StringComparison.Ordinal), "Failed-import redaction did not preserve an identifier placeholder.");
        Require(redacted.Contains("<local-path>", StringComparison.Ordinal), "Failed-import redaction did not preserve a filesystem placeholder.");
        Require(redacted.Contains("42 visible messages", StringComparison.Ordinal), "Failed-import redaction removed useful message-count diagnostics.");
        Require(redacted.Contains("1.23 s", StringComparison.Ordinal), "Failed-import redaction removed useful performance diagnostics.");
    }

    private static void RunRenamedStagingCancellationRegression()
    {
        var tempRoot = Path.GetTempPath();
        var output = Path.Combine(tempRoot, "llm-continuity-cancel-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var before = Directory.EnumerateDirectories(tempRoot, "llm-continuity-*").ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var rows = new[]
            {
                NewRecord("cancel-v23-a", "Cancel V23 A", 1_702_000_000),
                NewRecord("cancel-v23-b", "Cancel V23 B", 1_702_100_000)
            };

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            try
            {
                new ExportService(new ActivitySink())
                    .ExportAsync(rows, ExportFormat.ContinuationMarkdown, output, cancellationToken: cancelled.Token)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("Cancelled v2.3 bundle export unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            var after = Directory.EnumerateDirectories(tempRoot, "llm-continuity-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
            after.ExceptWith(before);
            after.Remove(output);

            Require(after.Count == 0, "Cancelled v2.3 export left a renamed staging directory behind.");
            Require(!Directory.EnumerateFiles(output).Any(), "Cancelled v2.3 export left output or temporary files behind.");
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); } catch { }
        }
    }

    private static void VerifyCiRuntimeLegalAssets()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var candidateRoots = new List<string>();
        var configuredRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
            candidateRoots.Add(configuredRoot);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(entry, "dotnet.exe")) || File.Exists(Path.Combine(entry, "dotnet")))
                    candidateRoots.Add(entry);
            }
            catch
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        var dotnetRoot = candidateRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(root => Directory.Exists(root)
                && Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Any(static name => name?.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase) == true));

        Require(!string.IsNullOrWhiteSpace(dotnetRoot),
            "GitHub Actions could not locate the .NET root that exposes LICENSE.txt required by stable packaging.");

        var licensePath = Directory.EnumerateFiles(dotnetRoot!, "*", SearchOption.TopDirectoryOnly)
            .First(file => Path.GetFileName(file).Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase));
        var noticesPath = Directory.EnumerateFiles(dotnetRoot!, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(file => Path.GetFileName(file).Equals("ThirdPartyNotices.txt", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Equals("THIRD-PARTY-NOTICES.TXT", StringComparison.OrdinalIgnoreCase));

        Require(!string.IsNullOrWhiteSpace(noticesPath),
            $"Pinned Windows .NET installation under '{dotnetRoot}' does not expose ThirdPartyNotices required by stable packaging.");

        var licenseText = File.ReadAllText(licensePath);
        Require(licenseText.Contains(".NET LIBRARY", StringComparison.OrdinalIgnoreCase),
            $"The Windows .NET LICENSE.txt under '{dotnetRoot}' did not identify the .NET Library License expected for Windows product distribution.");
        Require(new FileInfo(noticesPath!).Length > 0,
            "The Windows .NET ThirdPartyNotices file required by stable packaging was empty.");
    }

    private static ConversationRecord NewRecord(string id, string title, double created)
        => new()
        {
            Id = id,
            Title = title,
            CreateTimeRaw = created,
            UpdateTimeRaw = created + 100,
            Messages = new[] { new ConversationMessage(1, "user", "Synthetic cancellation fixture", created + 1, 0) }
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

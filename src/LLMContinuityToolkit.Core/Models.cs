using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LLMContinuityToolkit.Core;

public sealed record ConversationMessage(int Turn, string Role, string Text, double? CreateTime, int AttachmentCount);
public sealed record AttachmentReference(int Turn, string Marker);

public sealed class ConversationRecord
{
    private static readonly Regex InternalTimingRecapPattern = new(
        @"^(?:Thought|Worked) for (?:(?:about|around|roughly) )?(?:(?:\d+\s*(?:h|m|s))(?:\s+\d+\s*(?:h|m|s)){0,2}|(?:\d+\s+(?:hours?|minutes?|seconds?))(?:\s+(?:and\s+)?\d+\s+(?:hours?|minutes?|seconds?)){0,2}|a few seconds|a couple of seconds|a moment|less than a second|a second|a few minutes|a couple of minutes)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private IReadOnlyList<ConversationMessage> _messages = Array.Empty<ConversationMessage>();
    private int? _messageCount;
    private int? _userCount;
    private int? _assistantCount;
    private int? _attachmentCount;
    private double? _lastActiveMessageTimeRaw;
    private string? _finalHistoricalRole;

    public required string Id { get; init; }
    public required string Title { get; init; }
    public double? CreateTimeRaw { get; init; }
    public double? UpdateTimeRaw { get; init; }
    public required IReadOnlyList<ConversationMessage> Messages
    {
        get => _messages;
        init
        {
            if (!value.Any(static message => IsInternalTimingRecap(message.Role, message.Text)))
            {
                _messages = value;
                return;
            }

            _messages = value
                .Where(static message => !IsInternalTimingRecap(message.Role, message.Text))
                .Select(static (message, index) => message with { Turn = index + 1 })
                .ToArray();
        }
    }

    public IReadOnlyList<AttachmentReference> AttachmentReferences { get; init; } = Array.Empty<AttachmentReference>();

    public int MessageCount
    {
        get => _messageCount ?? Messages.Count;
        init => _messageCount = value;
    }
    public int UserCount
    {
        get => _userCount ??= Messages.Count(static message => message.Role == "user");
        init => _userCount = value;
    }
    public int AssistantCount
    {
        get => _assistantCount ??= Messages.Count(static message => message.Role == "assistant");
        init => _assistantCount = value;
    }
    public int AttachmentCount
    {
        get => _attachmentCount ??= Messages.Sum(static message => message.AttachmentCount);
        init => _attachmentCount = value;
    }
    public double? LastActiveMessageTimeRaw
    {
        get => _lastActiveMessageTimeRaw ?? (Messages.Count == 0 ? null : Messages[^1].CreateTime);
        init => _lastActiveMessageTimeRaw = value;
    }
    public string FinalHistoricalRole
    {
        get => _finalHistoricalRole ?? (Messages.Count == 0 ? "none" : Messages[^1].Role);
        init => _finalHistoricalRole = value;
    }

    public string? TranscriptFingerprint { get; internal set; }
    public string? RawRecordFingerprint { get; internal set; }

    public IReadOnlyList<string> UnsupportedVisibleContentTypes { get; internal set; } = Array.Empty<string>();
    public bool HasUnsupportedVisibleContent => UnsupportedVisibleContentTypes.Count > 0;

    public bool HasTranscript => MessageCount == Messages.Count;
    public string Created => TimestampUtil.FormatLocal(CreateTimeRaw);
    public string Updated => TimestampUtil.FormatLocal(UpdateTimeRaw);
    public string LastActiveMessage => LastActiveMessageTimeRaw is { } value ? TimestampUtil.FormatLocal(value) : "Unknown";

    internal static bool IsInternalTimingRecap(string role, string text)
    {
        if (!string.Equals(role, "assistant", StringComparison.Ordinal))
            return false;
        var normalized = text.Trim();
        return normalized.Length > 0
            && normalized.IndexOfAny(['\r', '\n']) < 0
            && InternalTimingRecapPattern.IsMatch(normalized);
    }
}

public sealed class CompatibilityReport
{
    public int ConversationRecordsFound { get; internal set; }
    public int ConversationsPrepared { get; internal set; }
    public int MissingCurrentNode { get; internal set; }
    public int MissingMapping { get; internal set; }
    public int BrokenActivePaths { get; internal set; }
    public int ActivePathCycles { get; internal set; }
    public int DuplicateConversationIds { get; internal set; }
    public int SkippedInvisibleMessages { get; internal set; }
    public int SkippedToolDirectedMessages { get; internal set; }
    public int SkippedAnalysisMessages { get; internal set; }
    public int SkippedStructuredReasoningMessages { get; internal set; }
    public int SkippedReasoningRecaps { get; internal set; }
    public int SkippedEmptyMessages { get; internal set; }
    public int UnknownStructuredContentTypes { get; internal set; }
    public int MalformedConversationRecords { get; internal set; }

    private readonly HashSet<string> _unknownTypes = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> UnknownContentTypes => _unknownTypes;

    internal void AddUnknownContentType(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && _unknownTypes.Add(value))
            UnknownStructuredContentTypes++;
    }
}

public sealed record ImportProgress(string Phase, int Current, int Total, string Detail, bool IsIndeterminate = false);
public sealed record ActivityEvent(DateTimeOffset Timestamp, string Category, string Message, ActivityLevel Level = ActivityLevel.Info)
{
    public string Display => $"{Timestamp:HH:mm:ss.fff}  [{Category.ToUpperInvariant()}]  {Message}";
}

public enum ActivityLevel { Info, Success, Warning, Error, Performance }

public sealed class ImportResult
{
    public required IReadOnlyList<ConversationRecord> Conversations { get; init; }
    public required CompatibilityReport Compatibility { get; init; }
    public required TimeSpan SourcePreparationTime { get; init; }
    public required TimeSpan TranscriptIndexingTime { get; init; }
    public required TimeSpan TotalTime { get; init; }
}

public enum ExportFormat { ContinuationMarkdown, Markdown, Html, PlainText, CompleteJson }
public sealed record ExportProgress(int Current, int Total, string Title, string Phase);

public sealed class ExportResult
{
    public required string OutputPath { get; init; }
    public required ExportFormat Format { get; init; }
    public required int ConversationCount { get; init; }
    public required int VerifiedCount { get; init; }
    public required int AttachmentReferenceCount { get; init; }
    public bool IsBundle { get; init; }
    public string? ContinuationPrompt { get; init; }
    public TimeSpan Elapsed { get; init; }
}

public sealed class ContinuationVerificationResult
{
    public bool Verified { get; init; }
    public bool StructuralVerified { get; init; }
    public bool HandoffVerified { get; init; }
    public int ExpectedTurns { get; init; }
    public int StartMarkers { get; init; }
    public int EndMarkers { get; init; }
    public int HeadingCount { get; init; }
    public int AttachmentReferences { get; init; }
}

public sealed class BundleVerificationResult
{
    public bool Verified { get; init; }
    public bool ManifestVerified { get; init; }
    public bool InstructionsVerified { get; init; }
    public int ExpectedEntries { get; init; }
    public int ActualEntries { get; init; }
    public int VerifiedPayloads { get; init; }
}

public sealed class BundleManifest
{
    [JsonPropertyName("format")] public string Format { get; init; } = "continuity-bundle-v2";
    [JsonPropertyName("bundle_schema")] public int BundleSchema { get; init; } = 1;
    [JsonPropertyName("application")] public string Application { get; init; } = AppInfo.Name;
    [JsonPropertyName("application_version")] public string ApplicationVersion { get; init; } = AppInfo.Version;
    [JsonPropertyName("developer")] public string Developer { get; init; } = AppInfo.Developer;
    [JsonPropertyName("generated_by")] public string GeneratedBy { get; init; } = AppInfo.DisplayName;
    [JsonPropertyName("generated_at")] public string GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    [JsonPropertyName("export_format")] public string ExportFormat { get; init; } = string.Empty;
    [JsonPropertyName("conversation_count")] public int ConversationCount { get; init; }
    [JsonPropertyName("payload_hash_algorithm")] public string PayloadHashAlgorithm { get; init; } = "SHA-256";
    [JsonPropertyName("instructions_file")] public string? InstructionsFile { get; init; }
    [JsonPropertyName("files")] public required IReadOnlyList<BundleManifestItem> Files { get; init; }
}

public sealed class BundleManifestItem
{
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("filename")] public required string FileName { get; init; }
    [JsonPropertyName("messages")] public int Messages { get; init; }
    [JsonPropertyName("user_messages")] public int UserMessages { get; init; }
    [JsonPropertyName("assistant_messages")] public int AssistantMessages { get; init; }
    [JsonPropertyName("attachment_references")] public int AttachmentReferences { get; init; }
    [JsonPropertyName("last_active_message")] public required string LastActiveMessage { get; init; }
    [JsonPropertyName("sha256")] public required string Sha256 { get; init; }
}

public static class AppInfo
{
    public const string Name = "LLM Continuity Toolkit";
    public const string Developer = "DevMoarRamz";
    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version is { } version
        ? $"{version.Major}.{version.Minor}.{version.Build}"
        : "unknown";
    public static string DisplayName => $"{Name} {Version}";
}

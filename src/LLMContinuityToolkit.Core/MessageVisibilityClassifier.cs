using System.Text.Json;

namespace LLMContinuityToolkit.Core;

internal enum MessageDisposition
{
    Visible,
    UnsupportedRole,
    HiddenMetadata,
    ToolDirected,
    AnalysisChannel,
    ReasoningContent,
    ReasoningRecap
}

internal readonly record struct MessageClassification(MessageDisposition Disposition, string Role)
{
    public bool IsVisible => Disposition == MessageDisposition.Visible;
}

internal static class MessageVisibilityClassifier
{
    private static readonly HashSet<string> InternalContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "analysis",
        "reasoning",
        "thought",
        "thoughts",
        "model_editable_context"
    };

    public static MessageClassification Classify(JsonElement message)
    {
        if (!message.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object)
            return new(MessageDisposition.UnsupportedRole, string.Empty);

        var role = ChatExportReader.GetString(author, "role") ?? string.Empty;
        if (role is not ("user" or "assistant"))
            return new(MessageDisposition.UnsupportedRole, role);

        if (message.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("is_visually_hidden_from_conversation", out var hidden)
            && IsTrue(hidden))
        {
            return new(MessageDisposition.HiddenMetadata, role);
        }

        var recipient = ChatExportReader.GetString(message, "recipient");
        if (!string.IsNullOrWhiteSpace(recipient)
            && !recipient.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return new(MessageDisposition.ToolDirected, role);
        }

        var channel = ChatExportReader.GetString(message, "channel");
        if (channel is not null
            && (channel.Equals("analysis", StringComparison.OrdinalIgnoreCase)
                || channel.Equals("reasoning", StringComparison.OrdinalIgnoreCase)))
        {
            return new(MessageDisposition.AnalysisChannel, role);
        }

        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            var contentType = ChatExportReader.GetString(content, "content_type")
                ?? ChatExportReader.GetString(content, "type")
                ?? string.Empty;

            if (contentType.Equals("reasoning_recap", StringComparison.OrdinalIgnoreCase))
                return new(MessageDisposition.ReasoningRecap, role);

            if (InternalContentTypes.Contains(contentType))
                return new(MessageDisposition.ReasoningContent, role);
        }

        return new(MessageDisposition.Visible, role);
    }

    public static void CountExclusion(MessageDisposition disposition, CompatibilityReport report)
    {
        switch (disposition)
        {
            case MessageDisposition.HiddenMetadata:
                report.SkippedInvisibleMessages++;
                break;
            case MessageDisposition.ToolDirected:
                report.SkippedToolDirectedMessages++;
                break;
            case MessageDisposition.AnalysisChannel:
                report.SkippedAnalysisMessages++;
                break;
            case MessageDisposition.ReasoningContent:
                report.SkippedStructuredReasoningMessages++;
                break;
            case MessageDisposition.ReasoningRecap:
                report.SkippedReasoningRecaps++;
                break;
        }
    }

    private static bool IsTrue(JsonElement element)
        => element.ValueKind == JsonValueKind.True
            || (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) && number != 0)
            || (element.ValueKind == JsonValueKind.String
                && (bool.TryParse(element.GetString(), out var flag) && flag
                    || long.TryParse(element.GetString(), out var numeric) && numeric != 0));
}

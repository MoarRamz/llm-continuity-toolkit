using System.Text.RegularExpressions;

namespace LLMContinuityToolkit.Core;

public static class DiagnosticRedactor
{
    // The standard regex engine is required because the Windows path detector uses a fixed-width lookbehind.
    private static readonly Regex WindowsPathPattern = new(
        @"(?<![A-Za-z0-9_])(?:[A-Za-z]:\\|\\\\)(?:[^\r\n<>\""|?*]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex IndexTitlePattern = new(
        @"^\d+\s{2}(?<title>.+) — \d+ visible messages$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex CompatibilityTitlePattern = new(
        @"^(?<title>.+): readable exports blocked until support is added",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex QuotedConversationValuePattern = new(
        @"(?i:(?:conversation(?: ID)?|selected conversation))\s+'(?<value>[^']+)'",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static void RegisterSensitiveValues(ActivitySink sink, ActivityEvent entry)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(entry);
        var message = entry.Message;

        if (entry.Category.Equals("INDEX", StringComparison.OrdinalIgnoreCase))
        {
            var match = IndexTitlePattern.Match(message);
            if (match.Success)
                sink.RegisterTitle(match.Groups["title"].Value);
        }

        if (entry.Category.Equals("COMPAT", StringComparison.OrdinalIgnoreCase))
        {
            var match = CompatibilityTitlePattern.Match(message);
            if (match.Success)
                sink.RegisterTitle(match.Groups["title"].Value);
        }

        foreach (Match match in QuotedConversationValuePattern.Matches(message))
            sink.RegisterIdentifier(match.Groups["value"].Value);

        foreach (Match match in WindowsPathPattern.Matches(message))
            sink.RegisterPath(match.Value);
    }

    public static string Redact(string text, ActivitySensitiveSnapshot sensitive)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sensitive);

        var result = text;
        for (var i = 0; i < sensitive.Titles.Count; i++)
            result = result.Replace(sensitive.Titles[i], $"Conversation {i + 1:D3}", StringComparison.Ordinal);

        foreach (var identifier in sensitive.Identifiers)
            result = result.Replace(identifier, "<conversation-id>", StringComparison.Ordinal);

        foreach (var path in sensitive.Paths)
            result = result.Replace(path, "<local-path>", StringComparison.OrdinalIgnoreCase);

        return WindowsPathPattern.Replace(result, "<local-path>");
    }
}

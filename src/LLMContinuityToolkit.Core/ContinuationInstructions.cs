using System.Text;

namespace LLMContinuityToolkit.Core;

public static class ContinuationInstructions
{
    private const string BundleTail = "Read each file's handoff metadata and continuation guidance first. Determine the relationships and chronology among the files from their metadata and transcript content. When files clearly form a continuation chain, reconstruct that chain in chronological order and treat later project state, decisions, and completed work as authoritative where they supersede earlier history. If the files represent multiple unrelated conversations, projects, or continuation chains, keep those histories separate rather than merging them. For any thread we continue, resume from that thread's latest relevant historical turn instead of restarting or summarizing unless I ask. Preserve established decisions, terminology, constraints, preferences, workflow, and completed work. If a referenced attachment is missing and materially needed, tell me exactly which attachment you need rather than guessing.";

    public static string ForBundle(int conversationCount)
    {
        if (conversationCount < 2)
            throw new ArgumentOutOfRangeException(nameof(conversationCount), "Bundle instructions require at least two conversations.");

        return $"Continue using the attached Continuation ZIP archive as historical context. It contains {conversationCount} Continuation Markdown files. {BundleTail}";
    }

    public static string BuildBundleReadMe(int conversationCount, DateTimeOffset generatedAt)
    {
        var prompt = ForBundle(conversationCount);
        var builder = new StringBuilder(2048);
        builder.AppendLine(AppInfo.DisplayName);
        builder.AppendLine($"Developed by {AppInfo.Developer}");
        builder.AppendLine("Continuation Bundle Instructions");
        builder.AppendLine();
        builder.AppendLine($"Continuation files in this archive: {conversationCount}");
        builder.AppendLine($"Generated: {generatedAt:yyyy-MM-dd HH:mm}");
        builder.AppendLine();
        builder.AppendLine("INSTRUCTIONS FOR CHATLLM");
        builder.AppendLine("------------------------");
        builder.AppendLine(prompt);
        return builder.ToString();
    }
}

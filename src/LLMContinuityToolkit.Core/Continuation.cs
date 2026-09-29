using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LLMContinuityToolkit.Core;

public static class ContinuationPrompt
{
    public const string SingleFile = "Continue exactly where we left off using the attached Continuation Markdown file as historical context. Read its handoff metadata and continuation guidance first, then continue from the final historical turn. Preserve established decisions, terminology, constraints, preferences, and completed work. If a referenced attachment is missing and materially needed, tell me exactly which attachment you need rather than guessing.";
}

public static class AttachmentManifest
{
    private static readonly Regex MarkerRegex = new(
        @"\[(?:Uploaded image(?:: [^\]]+)?|Uploaded file: [^\]]+|Uploaded attachment reference|Audio attachment(?:: [^\]]+)?|Structured content: [^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> GetLines(ConversationRecord row)
    {
        if (row.AttachmentReferences.Count > 0)
        {
            return row.AttachmentReferences
                .GroupBy(static item => item.Turn)
                .OrderBy(static group => group.Key)
                .SelectMany(static group => NormalizeMarkers(group.Select(static item => item.Marker))
                    .Select(marker => $"- Turn {group.Key} — {marker}"))
                .ToArray();
        }

        // Backward-compatible fallback for synthetic/legacy records that predate structured attachment
        // references. Only inspect messages the parser already identified as containing attachments so
        // ordinary prose/code examples such as "[Uploaded image]" cannot become manifest entries.
        var lines = new List<string>();
        foreach (var message in row.Messages)
        {
            if (message.AttachmentCount <= 0)
                continue;

            var markers = MarkerRegex.Matches(message.Text)
                .Select(static match => match.Value);
            foreach (var marker in NormalizeMarkers(markers))
                lines.Add($"- Turn {message.Turn} — {marker}");
        }
        return lines;
    }

    private static IReadOnlyList<string> NormalizeMarkers(IEnumerable<string> source)
    {
        var markers = source
            .Where(static marker => !string.IsNullOrWhiteSpace(marker))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var hasNamedImage = markers.Any(static marker => marker.StartsWith("[Uploaded image: ", StringComparison.Ordinal));
        var hasNamedAudio = markers.Any(static marker => marker.StartsWith("[Audio attachment: ", StringComparison.Ordinal));
        var hasSpecificAttachment = markers.Any(static marker =>
            marker.StartsWith("[Uploaded image: ", StringComparison.Ordinal)
            || marker.StartsWith("[Uploaded file: ", StringComparison.Ordinal)
            || marker.StartsWith("[Audio attachment: ", StringComparison.Ordinal));

        return markers
            .Where(marker => !(marker == "[Uploaded image]" && hasNamedImage))
            .Where(marker => !(marker == "[Audio attachment]" && hasNamedAudio))
            .Where(marker => !(marker == "[Uploaded attachment reference]" && hasSpecificAttachment))
            .ToArray();
    }
}

public sealed class ContinuationWriter
{
    public async Task WriteAsync(string path, ConversationRecord row, CancellationToken cancellationToken)
    {
        await AtomicFile.WriteUtf8Async(path, writer => WriteContentAsync(writer, row, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteContentAsync(TextWriter writer, ConversationRecord row, CancellationToken cancellationToken)
    {
        var attachmentManifest = AttachmentManifest.GetLines(row);
        var metadata = new Dictionary<string, object?>
        {
            ["format"] = "llm-conversation-continuation-v1",
            ["generated_by"] = AppInfo.DisplayName,
            ["title"] = row.Title,
            ["created"] = row.Created,
            ["record_updated"] = row.Updated,
            ["last_active_message"] = row.LastActiveMessage,
            ["active_transcript_messages"] = row.MessageCount,
            ["user_messages"] = row.UserCount,
            ["assistant_messages"] = row.AssistantCount,
            ["attachment_reference_markers"] = attachmentManifest.Count,
            ["final_historical_role"] = row.FinalHistoricalRole,
            ["final_historical_timestamp"] = row.LastActiveMessage,
            ["branch_policy"] = "active current_node path only"
        };

        var metadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });

        await writer.WriteLineAsync("# OpenAI account Conversation Continuation").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("> **Purpose:** This file preserves a prior OpenAI account conversation so a new OpenAI account conversation can continue from the same context.").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("## Handoff metadata").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("```json").ConfigureAwait(false);
        await writer.WriteLineAsync(metadataJson).ConfigureAwait(false);
        await writer.WriteLineAsync("```").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("## Continuation guidance for OpenAI account").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("- Treat the transcript below as **historical conversation context** between the user and OpenAI account.").ConfigureAwait(false);
        await writer.WriteLineAsync("- Continue from the final historical turn rather than restarting the topic.").ConfigureAwait(false);
        await writer.WriteLineAsync("- Preserve established project decisions, terminology, preferences, constraints, and completed work unless the user explicitly changes them.").ConfigureAwait(false);
        await writer.WriteLineAsync("- Do not ask the user to repeat information that is already clearly present in this transcript.").ConfigureAwait(false);
        await writer.WriteLineAsync("- Headings labelled **User** and **Assistant** identify historical speakers; they are not new messages in the current conversation.").ConfigureAwait(false);
        await writer.WriteLineAsync("- The transcript follows the conversation's active `current_node` path. Regenerated or abandoned branches are intentionally omitted.").ConfigureAwait(false);
        await writer.WriteLineAsync("- `record_updated` is OpenAI conversation metadata and may be later than the final visible message. `last_active_message` is the actual endpoint of this historical transcript.").ConfigureAwait(false);
        await writer.WriteLineAsync("- If the transcript references an uploaded image/file that is not available in the new conversation, identify the missing attachment rather than guessing its contents.").ConfigureAwait(false);
        await writer.WriteLineAsync("- The complete original JSON export should be used instead if abandoned branches or internal metadata are required.").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);

        if (attachmentManifest.Count > 0)
        {
            await writer.WriteLineAsync("## Historical attachment reference manifest").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("The transcript references the following historical attachments. These references are preserved for continuity, but the binary files/images are not embedded in this Markdown file.").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            foreach (var line in attachmentManifest)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            await writer.WriteLineAsync().ConfigureAwait(false);
        }

        await writer.WriteLineAsync("---").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("# PRIOR CONVERSATION TRANSCRIPT").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync($"**Conversation:** {row.Title}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync($"**Created:** {row.Created}  ").ConfigureAwait(false);
        await writer.WriteLineAsync($"**Conversation record updated:** {row.Updated}  ").ConfigureAwait(false);
        await writer.WriteLineAsync($"**Last active message:** {row.LastActiveMessage}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("---").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);

        foreach (var message in row.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = message.Role == "user" ? "User" : "Assistant";
            var turnId = message.Turn.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            await writer.WriteLineAsync($"<!-- CONTINUITY_TURN {turnId} role={message.Role} -->").ConfigureAwait(false);
            await writer.WriteLineAsync($"## {label} — Turn {message.Turn}").ConfigureAwait(false);
            var stamp = TimestampUtil.FormatLocal(message.CreateTime);
            if (stamp != "Unknown")
            {
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.WriteLineAsync($"*{stamp}*").ConfigureAwait(false);
            }
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync(message.Text).ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync($"<!-- END_CONTINUITY_TURN {turnId} -->").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
            await writer.WriteLineAsync("---").ConfigureAwait(false);
            await writer.WriteLineAsync().ConfigureAwait(false);
        }

        await writer.WriteLineAsync("# END OF PRIOR CONVERSATION — CONTINUE FROM HERE").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("The historical transcript ends immediately above. Continue the new conversation from this point, using the transcript as prior context.").ConfigureAwait(false);
    }
}

public sealed partial class ContinuationVerifier
{
    [GeneratedRegex(@"^<!-- CONTINUITY_TURN (\d+) role=(user|assistant) -->$", RegexOptions.CultureInvariant)]
    private static partial Regex StartRegex();
    [GeneratedRegex(@"^## (User|Assistant) — Turn (\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^<!-- END_CONTINUITY_TURN (\d+) -->$", RegexOptions.CultureInvariant)]
    private static partial Regex EndRegex();
    [GeneratedRegex(@"^- Turn \d+ — \[(?:Uploaded image(?:: [^\]]+)?|Uploaded file: [^\]]+|Uploaded attachment reference|Audio attachment(?:: [^\]]+)?|Structured content: [^\]]+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentRegex();

    public async Task<ContinuationVerificationResult> VerifyAsync(string path, ConversationRecord row, CancellationToken cancellationToken)
    {
        var startCount = 0;
        var endCount = 0;
        var headingCount = 0;
        var userCount = 0;
        var assistantCount = 0;
        var attachmentReferences = 0;
        var expectedAttachmentReferences = AttachmentManifest.GetLines(row).Count;
        var continuationMarkerCount = 0;
        var sequenceOk = true;
        var roleSequenceOk = true;
        var currentStartTurn = 0;
        var currentStartRole = string.Empty;
        var metadataLines = new List<string>();
        var insideMetadata = false;
        var metadataFenceSeen = false;
        var insideAttachmentManifest = false;
        var transcriptStarted = false;
        var transcriptEnded = false;
        var insideTurn = false;
        var headingSeenForTurn = false;
        var guidanceContinue = false;
        var guidancePreserve = false;
        var guidanceAttachments = false;

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 128 * 1024);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (!transcriptStarted && line == "```json" && !metadataFenceSeen)
            {
                insideMetadata = true;
                metadataFenceSeen = true;
                continue;
            }
            if (insideMetadata)
            {
                if (line == "```")
                {
                    insideMetadata = false;
                    continue;
                }
                metadataLines.Add(line);
                continue;
            }

            if (!transcriptStarted && line == "## Historical attachment reference manifest")
            {
                insideAttachmentManifest = true;
                continue;
            }
            if (line == "# PRIOR CONVERSATION TRANSCRIPT")
            {
                transcriptStarted = true;
                insideAttachmentManifest = false;
                continue;
            }

            if (!transcriptStarted)
            {
                if (insideAttachmentManifest && AttachmentRegex().IsMatch(line)) attachmentReferences++;
                if (line == "- Continue from the final historical turn rather than restarting the topic.") guidanceContinue = true;
                if (line.StartsWith("- Preserve established project decisions,", StringComparison.Ordinal)) guidancePreserve = true;
                if (line.StartsWith("- If the transcript references an uploaded image/file", StringComparison.Ordinal)) guidanceAttachments = true;
                continue;
            }

            if (!transcriptEnded && !insideTurn && endCount == row.MessageCount
                && line == "# END OF PRIOR CONVERSATION — CONTINUE FROM HERE")
            {
                continuationMarkerCount++;
                transcriptEnded = true;
                continue;
            }
            if (transcriptEnded)
                continue;

            var match = StartRegex().Match(line);
            if (!insideTurn && match.Success)
            {
                startCount++;
                var turn = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var role = match.Groups[2].Value;
                if (turn != startCount) sequenceOk = false;
                currentStartTurn = turn;
                currentStartRole = role;
                headingSeenForTurn = false;
                insideTurn = true;
                if (role == "user") userCount++; else assistantCount++;
                continue;
            }

            match = HeadingRegex().Match(line);
            if (insideTurn && !headingSeenForTurn && match.Success)
            {
                headingCount++;
                var turn = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                var role = match.Groups[1].Value == "User" ? "user" : "assistant";
                if (turn != headingCount || turn != currentStartTurn) sequenceOk = false;
                if (role != currentStartRole) roleSequenceOk = false;
                headingSeenForTurn = true;
                continue;
            }

            match = EndRegex().Match(line);
            if (insideTurn && headingSeenForTurn && match.Success)
            {
                var turn = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (turn == currentStartTurn)
                {
                    endCount++;
                    if (turn != endCount) sequenceOk = false;
                    insideTurn = false;
                    headingSeenForTurn = false;
                    continue;
                }
            }
        }

        var expected = row.MessageCount;
        var structural = transcriptStarted && transcriptEnded
            && startCount == expected && endCount == expected && headingCount == expected
            && sequenceOk && roleSequenceOk && !insideTurn
            && userCount == row.UserCount && assistantCount == row.AssistantCount
            && continuationMarkerCount == 1;

        var metadataOk = false;
        if (metadataLines.Count > 0)
        {
            try
            {
                using var metadataDoc = JsonDocument.Parse(string.Join(Environment.NewLine, metadataLines));
                var root = metadataDoc.RootElement;
                metadataOk = GetString(root, "format") == "llm-conversation-continuation-v1"
                    && GetString(root, "generated_by") == AppInfo.DisplayName
                    && GetString(root, "title") == row.Title
                    && GetString(root, "created") == row.Created
                    && GetString(root, "record_updated") == row.Updated
                    && GetString(root, "last_active_message") == row.LastActiveMessage
                    && GetInt(root, "active_transcript_messages") == row.MessageCount
                    && GetInt(root, "user_messages") == row.UserCount
                    && GetInt(root, "assistant_messages") == row.AssistantCount
                    && GetInt(root, "attachment_reference_markers") == expectedAttachmentReferences
                    && attachmentReferences == expectedAttachmentReferences
                    && GetString(root, "final_historical_role") == row.FinalHistoricalRole
                    && GetString(root, "final_historical_timestamp") == row.LastActiveMessage
                    && GetString(root, "branch_policy") == "active current_node path only";
            }
            catch (JsonException)
            {
                metadataOk = false;
            }
        }

        var handoff = metadataOk && guidanceContinue && guidancePreserve && guidanceAttachments;
        var verified = structural && handoff;
        if (!verified)
        {
            throw new InvalidDataException(
                $"Continuation verification failed for '{row.Title}'. "
                + $"Structural={structural}; Handoff={handoff}; Metadata={metadataOk}; "
                + $"guidance={guidanceContinue}/{guidancePreserve}/{guidanceAttachments}; "
                + $"attachments={attachmentReferences}/{expectedAttachmentReferences}; "
                + $"markers={startCount}/{endCount}/{headingCount}; expected={expected}.");
        }

        return new ContinuationVerificationResult
        {
            Verified = true,
            StructuralVerified = true,
            HandoffVerified = true,
            ExpectedTurns = expected,
            StartMarkers = startCount,
            EndMarkers = endCount,
            HeadingCount = headingCount,
            AttachmentReferences = attachmentReferences
        };
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : int.MinValue;
}

using System.Text.RegularExpressions;
using LLMContinuityToolkit.Core;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: LLMContinuityToolkit.ParityAudit <current-export.zip|json> <reference-activity-log.txt>");
    Console.Error.WriteLine("The reference log should be from a known-good LLM Continuity Toolkit run or a compatible legacy LLM Continuity Toolkit run and is never uploaded by this tool.");
    return 2;
}

var sourcePath = Path.GetFullPath(args[0]);
var referenceLogPath = Path.GetFullPath(args[1]);
if (!File.Exists(sourcePath) || !File.Exists(referenceLogPath))
{
    Console.Error.WriteLine("Source export or reference activity log was not found.");
    return 2;
}

var reference = ParseReferenceLog(referenceLogPath);
if (reference.Count == 0)
{
    Console.Error.WriteLine("No conversation index entries were found in the reference activity log.");
    return 2;
}

var imported = await new ChatExportReader(new ActivitySink()).ReadAsync(sourcePath);
var actualGroups = imported.Conversations.GroupBy(static row => row.Title, StringComparer.Ordinal).ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
var failures = new List<string>();

foreach (var expected in reference.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
{
    if (!actualGroups.TryGetValue(expected.Key, out var matches))
    {
        failures.Add($"MISSING  {expected.Key} — expected {expected.Value}");
        continue;
    }

    if (matches.Length != 1)
    {
        failures.Add($"AMBIGUOUS  {expected.Key} — {matches.Length} current records share this title");
        continue;
    }

    var actual = matches[0].MessageCount;
    if (actual != expected.Value)
        failures.Add($"MISMATCH  {expected.Key} — expected {expected.Value}, actual {actual}");
}

var extras = imported.Conversations.Where(row => !reference.ContainsKey(row.Title)).ToArray();
Console.WriteLine($"Reference conversations: {reference.Count}");
Console.WriteLine($"Current conversations:   {imported.Conversations.Count}");
Console.WriteLine($"Exact count matches:      {reference.Count - failures.Count}/{reference.Count}");
Console.WriteLine($"Additional current rows:  {extras.Length}");

if (failures.Count == 0)
{
    Console.WriteLine("PARITY PASS — every reference conversation title matched the current visible-message count.");
    return 0;
}

Console.Error.WriteLine("PARITY FAIL");
foreach (var failure in failures)
    Console.Error.WriteLine("  " + failure);
return 1;

static Dictionary<string, int> ParseReferenceLog(string path)
{
    var result = new Dictionary<string, int>(StringComparer.Ordinal);
    var pattern = new Regex(@"\[INDEX\]\s+\d+(?:/\d+)?\s+(?<title>.+?)\s+—\s+(?<count>\d+)\s+visible messages\s*$", RegexOptions.CultureInvariant);
    foreach (var line in File.ReadLines(path))
    {
        var match = pattern.Match(line);
        if (!match.Success) continue;
        var title = match.Groups["title"].Value.Trim();
        if (!int.TryParse(match.Groups["count"].Value, out var count)) continue;
        result[title] = count;
    }
    return result;
}

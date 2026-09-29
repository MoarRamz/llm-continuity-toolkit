using System.Text;
using LLMContinuityToolkit.Core;
using Microsoft.Win32;

namespace LLMContinuityToolkit.App;

public partial class MainWindow
{
    private void RegisterSensitiveValuesFromActivity(ActivityEvent entry)
        => DiagnosticRedactor.RegisterSensitiveValues(_activity, entry);

    private void RebuildRedactionRegistryForCurrentState()
    {
        _activity.ClearSensitiveValues();
        if (!string.IsNullOrWhiteSpace(_sourcePath))
        {
            _activity.RegisterPath(_sourcePath);
            _activity.RegisterPath(Path.GetFileName(_sourcePath));
        }

        foreach (var row in _rows)
        {
            _activity.RegisterTitle(row.Record.Title);
            _activity.RegisterIdentifier(row.Record.Id);
        }
    }

    private void SaveRedactedLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Redacted Activity Log",
            Filter = "Text file (*.txt)|*.txt",
            FileName = $"LLM_Continuity_Toolkit_Redacted_Activity_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;

        var sensitive = _activity.GetSensitiveSnapshot();
        var builder = new StringBuilder();
        builder.AppendLine($"{AppInfo.DisplayName} — Redacted Activity Log");
        builder.AppendLine($"Developed by {AppInfo.Developer}");
        builder.AppendLine($"Saved by explicit user request: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine("Conversation titles, conversation identifiers, and local filesystem values are pseudonymized. Performance, compatibility, memory, counts, and verification diagnostics are preserved.");

        foreach (var entry in _activityHistory)
            builder.AppendLine(DiagnosticRedactor.Redact(entry.Display, sensitive));

        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(false));
        _activity.Write("LOG", "Redacted activity log saved by explicit user request.", ActivityLevel.Success);
    }
}

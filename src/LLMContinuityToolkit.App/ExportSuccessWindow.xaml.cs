using System.Diagnostics;
using System.Windows;
using LLMContinuityToolkit.Core;

namespace LLMContinuityToolkit.App;

public partial class ExportSuccessWindow : Window
{
    private readonly ExportResult _result;

    public ExportSuccessWindow(ExportResult result)
    {
        InitializeComponent();
        _result = result;
        var continuation = result.Format == ExportFormat.ContinuationMarkdown;
        Headline.Text = continuation
            ? result.IsBundle ? "Your continuation archive is ready" : "Your Continuation Markdown file is ready"
            : result.IsBundle ? "Your export archive is ready" : "Export complete";
        Summary.Text = result.IsBundle
            ? $"{result.ConversationCount} conversation files were packaged successfully. {result.VerifiedCount} verified output(s)."
            : "1 file finalized successfully.";
        AttachmentSummary.Text = continuation
            ? $"Historical attachment references detected: {result.AttachmentReferenceCount}" + (result.IsBundle ? " • Continuation instructions included in archive." : string.Empty)
            : string.Empty;
        AttachmentSummary.Visibility = continuation ? Visibility.Visible : Visibility.Collapsed;
        PathText.Text = result.OutputPath;
        HelpText.Text = result.IsBundle
            ? "Open the folder to upload the ZIP archive directly into OpenAI account or move it as one self-contained package."
            : "Open the folder to use the exported file.";
        CopyPromptButton.Visibility = continuation ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyPromptButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_result.ContinuationPrompt)) return;
        Clipboard.SetText(_result.ContinuationPrompt);
        CopyPromptButton.Content = "Prompt Copied";
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_result.OutputPath);
        if (string.IsNullOrWhiteSpace(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}

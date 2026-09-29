using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LLMContinuityToolkit.Core;
using Microsoft.Win32;

namespace LLMContinuityToolkit.App;

public partial class MainWindow : Window
{
    private const int ActivityLimit = 2000;

    private readonly ObservableCollection<ConversationRowViewModel> _rows = new();
    private readonly ObservableCollection<ActivityEvent> _activityItems = new();
    private readonly List<ActivityEvent> _activityHistory = new(ActivityLimit);
    private readonly ActivitySink _activity = new();
    private ICollectionView? _rowsView;
    private CancellationTokenSource? _operationCancellation;
    private string? _sourcePath;
    private bool _busy;
    private bool _logPaused;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"{AppInfo.Name} {AppInfo.Version}";
        VersionText.Text = $"v{AppInfo.Version} • C# / .NET 10";
        ConversationGrid.ItemsSource = _rows;
        ActivityList.ItemsSource = _activityItems;
        _activity.Activity += Activity_Activity;
        _activity.Write("APP", $"{AppInfo.DisplayName} started.");
        _activity.Write("APP", $"Developed by {AppInfo.Developer}.");
        _activity.Write("APP", "No cache, application telemetry, background service, updater, or persistent activity log is enabled.");
        UpdateSelectionStatus();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
        => new AboutWindow { Owner = this }.ShowDialog();

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Select OpenAI account Data Export",
            Filter = "OpenAI account export (*.zip)|*.zip|Conversation JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        _sourcePath = dialog.FileName;
        _activity.RegisterPath(_sourcePath);
        _activity.RegisterPath(Path.GetFileName(_sourcePath));
        _rows.Clear();
        _rowsView = null;
        SetBusy(true, "Analyzing export...");
        ImportHeadline.Text = "Analyzing export...";
        ImportDetail.Text = Path.GetFileName(_sourcePath);
        _activity.Write("IMPORT", $"Selected {Path.GetFileName(_sourcePath)} ({new FileInfo(_sourcePath).Length / 1024d / 1024d:F1} MB).");

        _operationCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ImportProgress>(p =>
                StatusText.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Phase : p.Detail);
            var store = new LazyConversationStore(_activity);
            var result = await store.ReadMetadataAsync(_sourcePath, progress, _operationCancellation.Token);

            foreach (var record in result.Conversations)
            {
                _activity.RegisterTitle(record.Title);
                _activity.RegisterIdentifier(record.Id);
                var row = new ConversationRowViewModel(record);
                row.PropertyChanged += Row_PropertyChanged;
                _rows.Add(row);
            }

            _rowsView = CollectionViewSource.GetDefaultView(_rows);
            _rowsView.Filter = FilterConversation;
            _rowsView.Refresh();

            ImportHeadline.Text = $"{_rows.Count} conversations ready";
            ImportDetail.Text = Path.GetFileName(_sourcePath);
            StatusText.Text = $"Import completed in {result.TotalTime.TotalSeconds:F2} s.";
            _activity.Write("IMPORT", $"{_rows.Count} conversations ready for selection.", ActivityLevel.Success);
            _activity.Write("PERF", $"Import completed in {result.TotalTime.TotalSeconds:F2} s", ActivityLevel.Performance);
            CompactMemoryBoundary("Post-import");
        }
        catch (OperationCanceledException)
        {
            ResetImportedState();
            StatusText.Text = "Import cancelled.";
            _activity.Write("IMPORT", "Import cancelled; temporary resources released.", ActivityLevel.Warning);
            CompactMemoryBoundary("Post-cancel");
        }
        catch (Exception ex)
        {
            ResetImportedState();
            StatusText.Text = "Import failed.";
            _activity.Write("ERROR", $"Import failed: {ex.Message}", ActivityLevel.Error);
            CompactMemoryBoundary("Post-failure");
            MessageBox.Show(this, ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false, StatusText.Text);
            UpdateSelectionStatus();
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selectedMetadata = _rows.Where(static row => row.IsSelected).Select(static row => row.Record).ToArray();
        if (selectedMetadata.Length == 0) return;

        var folderDialog = new OpenFolderDialog { Title = "Choose Export Folder", Multiselect = false };
        if (folderDialog.ShowDialog(this) != true) return;
        _activity.RegisterPath(folderDialog.FolderName);

        if (FormatCombo.SelectedItem is not ComboBoxItem item
            || item.Tag is not string tag
            || !Enum.TryParse<ExportFormat>(tag, out var format))
            return;

        SetBusy(true, "Preparing export...");
        _operationCancellation = new CancellationTokenSource();
        IReadOnlyList<ConversationRecord> exportRecords = Array.Empty<ConversationRecord>();
        try
        {
            var progress = new Progress<ExportProgress>(p =>
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = p.Total == 0 ? 0 : p.Current * 100d / p.Total;
                StatusText.Text = $"{p.Phase} {p.Current}/{p.Total}: {p.Title}";
            });

            if (format == ExportFormat.CompleteJson)
            {
                exportRecords = selectedMetadata;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_sourcePath))
                    throw new InvalidOperationException("The original OpenAI account export is required to reconstruct selected transcripts.");

                StatusText.Text = $"Reconstructing {selectedMetadata.Length} selected transcript(s)...";
                var store = new LazyConversationStore(_activity);
                exportRecords = await store.HydrateSelectedAsync(
                    _sourcePath,
                    selectedMetadata,
                    _operationCancellation.Token);
            }

            var exporter = new ExportService(_activity);
            var result = await exporter.ExportAsync(
                exportRecords,
                format,
                folderDialog.FolderName,
                _sourcePath,
                progress,
                _operationCancellation.Token);
            _activity.RegisterPath(result.OutputPath);

            StatusText.Text = result.IsBundle
                ? $"Export complete: {selectedMetadata.Length} conversations packaged into one ZIP."
                : "Export complete.";
            _activity.Write("PERF", $"Export completed in {result.Elapsed.TotalSeconds:F2} s", ActivityLevel.Performance);

            exportRecords = Array.Empty<ConversationRecord>();
            CompactMemoryBoundary("Post-export");
            new ExportSuccessWindow(result) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            exportRecords = Array.Empty<ConversationRecord>();
            StatusText.Text = "Export cancelled.";
            _activity.Write("EXPORT", "Export cancelled; temporary staging resources released.", ActivityLevel.Warning);
            CompactMemoryBoundary("Post-export-cancel");
        }
        catch (Exception ex)
        {
            exportRecords = Array.Empty<ConversationRecord>();
            StatusText.Text = "Export failed.";
            _activity.Write("ERROR", $"Export failed: {ex.Message}", ActivityLevel.Error);
            CompactMemoryBoundary("Post-export-failure");
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            SetBusy(false, StatusText.Text);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy || _operationCancellation is null) return;
        _activity.Write("APP", "Cancellation requested by user.", ActivityLevel.Warning);
        StatusText.Text = "Cancelling...";
        _operationCancellation.Cancel();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _rowsView?.Refresh();
        UpdateSelectionStatus();
    }

    private bool FilterConversation(object item)
    {
        if (item is not ConversationRowViewModel row) return false;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return true;

        return row.Record.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Record.Created.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Record.Updated.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in VisibleRows()) row.IsSelected = true;
        UpdateSelectionStatus();
    }

    private void ClearVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in VisibleRows()) row.IsSelected = false;
        UpdateSelectionStatus();
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
        UpdateSelectionStatus();
    }

    private IEnumerable<ConversationRowViewModel> VisibleRows()
        => _rowsView is null ? _rows : _rowsView.OfType<ConversationRowViewModel>();

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationRowViewModel.IsSelected))
            Dispatcher.BeginInvoke(UpdateSelectionStatus);
    }

    private void UpdateSelectionStatus()
    {
        var selected = _rows.Count(static row => row.IsSelected);
        var visible = _rowsView is null ? _rows.Count : _rowsView.OfType<ConversationRowViewModel>().Count();
        SelectionText.Text = _rows.Count == 0
            ? "0 conversations selected"
            : $"Showing {visible} of {_rows.Count} • {selected} selected";
        ExportButton.IsEnabled = !_busy && selected > 0;
        SelectVisibleButton.IsEnabled = !_busy && visible > 0;
        ClearVisibleButton.IsEnabled = !_busy && visible > 0 && selected > 0;
        ClearAllButton.IsEnabled = !_busy && selected > 0;
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ImportButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        FormatCombo.IsEnabled = !busy;
        ConversationGrid.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.IsIndeterminate = busy;
        if (!busy) ProgressBar.Value = 0;
        StatusText.Text = status;
        UpdateSelectionStatus();
    }

    private void ResetImportedState()
    {
        _rows.Clear();
        _rowsView = null;
        _sourcePath = null;
        SearchBox.Clear();
        ImportHeadline.Text = "No export loaded";
        ImportDetail.Text = "Choose the ZIP file supplied by OpenAI account Data Export.";
    }

    private void CompactMemoryBoundary(string label)
    {
        _activity.Write("MEMORY", OperationMemory.Snapshot($"{label} before cleanup"));
        var elapsed = OperationMemory.CompactTransientAllocations();
        _activity.Write("MEMORY", OperationMemory.Snapshot($"{label} after cleanup"));
        _activity.Write("PERF", $"{label} memory compaction completed in {elapsed.TotalMilliseconds:F0} ms", ActivityLevel.Performance);
    }

    private void Activity_Activity(object? sender, ActivityEvent e)
    {
        RegisterSensitiveValuesFromActivity(e);
        Dispatcher.BeginInvoke(() =>
        {
            _activityHistory.Add(e);
            if (_activityHistory.Count > ActivityLimit)
                _activityHistory.RemoveAt(0);

            if (_logPaused) return;
            _activityItems.Add(e);
            while (_activityItems.Count > ActivityLimit)
                _activityItems.RemoveAt(0);
            if (AutoScrollCheck.IsChecked == true && _activityItems.Count > 0)
                ActivityList.ScrollIntoView(_activityItems[^1]);
        });
    }

    private void PauseLog_Click(object sender, RoutedEventArgs e)
    {
        _logPaused = !_logPaused;
        PauseLogButton.Content = _logPaused ? "Resume" : "Pause";
        if (_logPaused) return;

        _activityItems.Clear();
        foreach (var entry in _activityHistory) _activityItems.Add(entry);
        if (AutoScrollCheck.IsChecked == true && _activityItems.Count > 0)
            ActivityList.ScrollIntoView(_activityItems[^1]);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_activityHistory.Count == 0) return;
        Clipboard.SetText(string.Join(Environment.NewLine, _activityHistory.Select(static entry => entry.Display)));
        _activity.Write("LOG", "Activity log copied to clipboard.", ActivityLevel.Success);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _activityHistory.Clear();
        _activityItems.Clear();
        RebuildRedactionRegistryForCurrentState();
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Activity Log",
            Filter = "Text file (*.txt)|*.txt",
            FileName = $"LLM_Continuity_Toolkit_Activity_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;

        var builder = new StringBuilder();
        builder.AppendLine($"{AppInfo.DisplayName} — Activity Log");
        builder.AppendLine($"Developed by {AppInfo.Developer}");
        builder.AppendLine($"Saved by explicit user request: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine("This log was memory-only until Save Log was selected.");
        foreach (var entry in _activityHistory) builder.AppendLine(entry.Display);
        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(false));
        _activity.Write("LOG", $"Activity log saved to {dialog.FileName}.", ActivityLevel.Success);
    }
}

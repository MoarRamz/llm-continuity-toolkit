namespace LLMContinuityToolkit.Core;

public sealed class LazyConversationStore
{
    private const long MaxConversationJsonBytes = 8L * 1024 * 1024 * 1024;
    private readonly ChatExportReader _reader;

    public LazyConversationStore(ActivitySink activity) => _reader = new ChatExportReader(activity);

    public Task<ImportResult> ReadMetadataAsync(
        string sourcePath,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDirectJsonSize(sourcePath);
        return _reader.ReadMetadataAsync(sourcePath, progress, cancellationToken);
    }

    public Task<IReadOnlyList<ConversationRecord>> HydrateSelectedAsync(
        string sourcePath,
        IReadOnlyList<ConversationRecord> selectedMetadata,
        CancellationToken cancellationToken = default)
    {
        ValidateDirectJsonSize(sourcePath);
        return _reader.HydrateSelectedAsync(sourcePath, selectedMetadata, cancellationToken);
    }

    private static void ValidateDirectJsonSize(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return;

        var info = new FileInfo(sourcePath);
        if (info.Length <= 0)
            throw new InvalidDataException("Conversation JSON is empty.");
        if (info.Length > MaxConversationJsonBytes)
            throw new InvalidDataException("Conversation JSON exceeds the supported safety limit.");
    }
}

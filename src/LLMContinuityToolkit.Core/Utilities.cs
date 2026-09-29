using System.Security.Cryptography;
using System.Text;

namespace LLMContinuityToolkit.Core;

public static class TimestampUtil
{
    public static string FormatLocal(double? value)
    {
        if (value is null)
            return "Unknown";

        try
        {
            var seconds = (long)Math.Floor(value.Value);
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return "Unknown";
        }
    }

    public static string DatePrefix(double? value)
    {
        if (value is null)
            return "Unknown Date";

        try
        {
            var seconds = (long)Math.Floor(value.Value);
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd");
        }
        catch
        {
            return "Unknown Date";
        }
    }
}

public static class FileNameUtil
{
    private static readonly HashSet<string> ReservedDeviceNames = BuildReservedDeviceNames();

    public static string SafeFileName(string value, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(value))
            value = "Untitled Conversation";
        if (maxLength < 8)
            throw new ArgumentOutOfRangeException(nameof(maxLength));

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
            if (builder.Length >= maxLength)
                break;
        }

        var result = NormalizeStem(builder.ToString());
        if (result.Length > maxLength)
            result = NormalizeStem(result[..maxLength]);

        if (string.IsNullOrWhiteSpace(result))
            result = "Untitled Conversation";

        var baseName = Path.GetFileNameWithoutExtension(result);
        if (ReservedDeviceNames.Contains(baseName))
            result = "_" + result;

        return result;
    }

    public static string UniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
            return desiredPath;

        var directory = Path.GetDirectoryName(desiredPath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var i = 2; i < 10_000; i++)
        {
            var suffix = $" ({i})";
            var safeStemLength = Math.Max(8, 120 - suffix.Length);
            var safeStem = SafeFileName(stem, safeStemLength);
            var candidate = Path.Combine(directory, safeStem + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        throw new IOException("Could not create a unique output filename after 9,998 collision attempts.");
    }

    private static string NormalizeStem(string value)
    {
        var result = value.Trim().TrimEnd(' ', '.');
        while (result.Contains("__", StringComparison.Ordinal))
            result = result.Replace("__", "_", StringComparison.Ordinal);
        return result;
    }

    private static HashSet<string> BuildReservedDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$"
        };
        for (var i = 1; i <= 9; i++)
        {
            names.Add($"COM{i}");
            names.Add($"LPT{i}");
        }
        return names;
    }
}

public static class HashUtil
{
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class AtomicFile
{
    public static async Task WriteUtf8Async(string destinationPath, Func<StreamWriter, Task> writerAction, CancellationToken cancellationToken)
    {
        await WriteStreamAsync(destinationPath, async (file, token) =>
        {
            await using var writer = new StreamWriter(file, new UTF8Encoding(false), 128 * 1024, leaveOpen: true);
            await writerAction(writer).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteStreamAsync(
        string destinationPath,
        Func<FileStream, CancellationToken, Task> streamAction,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Output path has no parent directory.");

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".llm-continuity-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await streamAction(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(tempPath).Length <= 0)
                throw new InvalidDataException("Export produced an empty temporary file.");

            File.Move(tempPath, destinationPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}

public sealed record ActivitySensitiveSnapshot(
    IReadOnlyList<string> Titles,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Identifiers);

public sealed class ActivitySink
{
    private readonly object _sensitiveGate = new();
    private readonly HashSet<string> _sensitiveTitles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sensitivePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sensitiveIdentifiers = new(StringComparer.Ordinal);

    public event EventHandler<ActivityEvent>? Activity;

    public void Write(string category, string message, ActivityLevel level = ActivityLevel.Info)
    {
        var entry = new ActivityEvent(DateTimeOffset.Now, category, message, level);
        DiagnosticRedactor.RegisterSensitiveValues(this, entry);
        Activity?.Invoke(this, entry);
    }

    public void RegisterTitle(string? value) => Register(_sensitiveTitles, value);
    public void RegisterPath(string? value) => Register(_sensitivePaths, value);
    public void RegisterIdentifier(string? value) => Register(_sensitiveIdentifiers, value);

    public ActivitySensitiveSnapshot GetSensitiveSnapshot()
    {
        lock (_sensitiveGate)
        {
            return new ActivitySensitiveSnapshot(
                _sensitiveTitles.OrderByDescending(static value => value.Length).ThenBy(static value => value, StringComparer.Ordinal).ToArray(),
                _sensitivePaths.OrderByDescending(static value => value.Length).ThenBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                _sensitiveIdentifiers.OrderByDescending(static value => value.Length).ThenBy(static value => value, StringComparer.Ordinal).ToArray());
        }
    }

    public void ClearSensitiveValues()
    {
        lock (_sensitiveGate)
        {
            _sensitiveTitles.Clear();
            _sensitivePaths.Clear();
            _sensitiveIdentifiers.Clear();
        }
    }

    private void Register(HashSet<string> target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        lock (_sensitiveGate)
            target.Add(value);
    }
}

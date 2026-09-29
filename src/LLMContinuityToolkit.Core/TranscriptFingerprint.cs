using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LLMContinuityToolkit.Core;

internal static class TranscriptFingerprint
{
    public static Accumulator CreateAccumulator() => new();

    public static string Compute(IReadOnlyList<ConversationMessage> messages)
    {
        using var accumulator = CreateAccumulator();
        foreach (var message in messages)
            accumulator.Append(message.Turn, message.Role, message.Text, message.CreateTime, message.AttachmentCount);
        return accumulator.Complete();
    }

    internal sealed class Accumulator : IDisposable
    {
        private readonly IncrementalHashWriteStream _sink = new(HashAlgorithmName.SHA256);
        private readonly StreamWriter _writer;
        private bool _completed;
        private string? _result;

        public Accumulator()
        {
            _writer = new StreamWriter(_sink, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                NewLine = "\n"
            };
            _writer.Write("continuity-visible-transcript-v2\n");
        }

        public void Append(int turn, string role, string text, double? createTime, int attachmentCount)
        {
            if (_completed)
                throw new InvalidOperationException("The transcript fingerprint has already been finalized.");

            WriteField(_writer, turn.ToString(CultureInfo.InvariantCulture));
            WriteField(_writer, role);
            WriteField(_writer, text);
            WriteField(_writer, createTime is { } timestamp
                ? BitConverter.DoubleToInt64Bits(timestamp).ToString(CultureInfo.InvariantCulture)
                : "null");
            WriteField(_writer, attachmentCount.ToString(CultureInfo.InvariantCulture));
        }

        public string Complete()
        {
            if (_result is not null)
                return _result;

            _writer.Flush();
            _completed = true;
            _result = Convert.ToHexString(_sink.GetHashAndReset()).ToLowerInvariant();
            return _result;
        }

        public void Dispose()
        {
            if (!_completed)
                Complete();
            _writer.Dispose();
            _sink.Dispose();
        }
    }

    private static void WriteField(TextWriter writer, string value)
    {
        writer.Write(value.Length.ToString(CultureInfo.InvariantCulture));
        writer.Write(':');
        writer.Write(value);
        writer.Write('\n');
    }
}

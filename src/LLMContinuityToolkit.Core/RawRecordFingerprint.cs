using System.Security.Cryptography;
using System.Text.Json;

namespace LLMContinuityToolkit.Core;

internal static class RawRecordFingerprint
{
    public static string Compute(JsonElement conversation)
    {
        if (conversation.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A conversation fingerprint requires a JSON object.", nameof(conversation));

        // Preserve the canonical compact-JSON fingerprint contract without allocating a complete
        // serialized byte array or routing every write through CryptoStream.
        using var sink = new IncrementalHashWriteStream(HashAlgorithmName.SHA256);
        using (var writer = new Utf8JsonWriter(sink, new JsonWriterOptions { Indented = false }))
        {
            conversation.WriteTo(writer);
            writer.Flush();
        }
        return Convert.ToHexString(sink.GetHashAndReset()).ToLowerInvariant();
    }
}

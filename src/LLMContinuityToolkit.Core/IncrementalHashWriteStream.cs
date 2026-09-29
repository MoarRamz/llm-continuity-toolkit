using System.Security.Cryptography;

namespace LLMContinuityToolkit.Core;

internal sealed class IncrementalHashWriteStream : Stream
{
    private readonly IncrementalHash _hash;
    private bool _disposed;

    public IncrementalHashWriteStream(HashAlgorithmName algorithm)
        => _hash = IncrementalHash.CreateHash(algorithm);

    public byte[] GetHashAndReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _hash.GetHashAndReset();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        _hash.AppendData(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hash.AppendData(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hash.Dispose();
        }
        base.Dispose(disposing);
    }
}

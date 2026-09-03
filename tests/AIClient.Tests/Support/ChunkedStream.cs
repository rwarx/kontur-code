using System.Text;

namespace AIClient.Tests.Support;

/// <summary>
/// A read-only stream that hands out one predefined chunk per read, however large the
/// caller's buffer is.
/// </summary>
/// <remarks>
/// Real streaming responses arrive in whatever pieces the network produced, which routinely
/// means a read that ends halfway through a JSON frame. A reader that assumes each read ends
/// on a line boundary silently drops tokens under that condition and looks fine in a test that
/// hands it the whole body at once. This forces the boundaries to land where the test wants them.
/// </remarks>
public sealed class ChunkedStream : Stream
{
    private readonly Queue<byte[]> _chunks;
    private byte[] _current = [];
    private int _offset;

    public ChunkedStream(IEnumerable<string> chunks) =>
        _chunks = new Queue<byte[]>(chunks.Select(c => Encoding.UTF8.GetBytes(c)));

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (_offset >= _current.Length)
        {
            if (_chunks.Count == 0)
            {
                return 0;
            }

            _current = _chunks.Dequeue();
            _offset = 0;
        }

        var take = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, take).CopyTo(buffer);
        _offset += take;
        return take;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer.AsSpan(offset, count)));
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

using System.Security.Cryptography;

namespace FFApp.Remote;

/// <summary>
/// Reads a window of a file for an upload chunk, feeding every byte into the running SHA-256 and
/// counting progress.
/// </summary>
internal sealed class HashingWindowStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly IncrementalHash _hash;
    private readonly Action<int> _onRead;
    private long _remaining;

    public HashingWindowStream(Stream inner, long length, IncrementalHash hash, Action<int> onRead)
    {
        _inner = inner;
        _length = length;
        _hash = hash;
        _onRead = onRead;
        _remaining = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
            return 0;
        var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        Account(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
            return 0;
        var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
        Account(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            if (_remaining > 0)
                throw new IOException("The source file became shorter during the upload.");
            return;
        }
        _hash.AppendData(data);
        _remaining -= data.Length;
        _onRead(data.Length);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Bytes-per-second over a sliding window, for "108 MB/s" in the queue.</summary>
internal sealed class TransferRate(TimeProvider timeProvider)
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private readonly Queue<(long Timestamp, long Bytes)> _samples = new();
    private long _total;

    public void Add(long bytes)
    {
        _total += bytes;
        var now = timeProvider.GetTimestamp();
        _samples.Enqueue((now, _total));
        while (_samples.Count > 2 && timeProvider.GetElapsedTime(_samples.Peek().Timestamp, now) > Window)
            _samples.Dequeue();
    }

    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
                return null;
            var first = _samples.Peek();
            var last = _samples.Last();
            var seconds = timeProvider.GetElapsedTime(first.Timestamp, last.Timestamp).TotalSeconds;
            return seconds > 0.2 ? (last.Bytes - first.Bytes) / seconds : null;
        }
    }
}

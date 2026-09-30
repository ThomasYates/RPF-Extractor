namespace RpfToFiveM.Core.Rpf;

/// <summary>
/// Read-only window onto part of a shared stream. Seeks before every read so
/// several windows over the same stream can be used one after another.
/// </summary>
internal sealed class SubStream : Stream
{
    private readonly Stream _base;
    private readonly long _start;
    private readonly long _length;
    private readonly Action<long>? _onRead;
    private long _position;

    public SubStream(Stream baseStream, long start, long length, Action<long>? onRead = null)
    {
        _base = baseStream;
        _start = start;
        _length = length;
        _onRead = onRead;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long remaining = _length - _position;
        if (remaining <= 0) return 0;
        if (count > remaining) count = (int)remaining;

        _base.Position = _start + _position;
        int read = _base.Read(buffer, offset, count);
        if (read == 0) throw new EndOfStreamException("Archive data ended unexpectedly.");
        _position += read;
        _onRead?.Invoke(read);
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

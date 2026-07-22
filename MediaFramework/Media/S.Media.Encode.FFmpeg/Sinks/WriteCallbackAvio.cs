using System.Runtime.InteropServices;

namespace S.Media.Encode.FFmpeg.Sinks;

/// <summary>
/// Write-side custom <c>AVIOContext</c> delivering muxed bytes to a managed callback (the write twin
/// of FFmpeg.Common's read-side <c>StreamAvioBridge</c>). Non-seekable by design - live containers
/// (mpegts) never seek back. The callback receives a copy of each chunk (safe to retain).
/// </summary>
internal sealed unsafe class WriteCallbackAvio : IDisposable
{
    private const int IoBufferSize = 32 * 1024;

    private static readonly avio_alloc_context_write_packet WritePacketCallback = WritePacket;

    private readonly GCHandle _holderHandle;
    private readonly AVIOContext* _avio;
    private readonly Holder _holder;
    private bool _disposed;

    public WriteCallbackAvio(Action<ReadOnlyMemory<byte>> onBytes)
    {
        ArgumentNullException.ThrowIfNull(onBytes);
        _holder = new Holder(onBytes);
        _holderHandle = GCHandle.Alloc(_holder);

        var buffer = (byte*)av_malloc(IoBufferSize);
        if (buffer is null)
        {
            _holderHandle.Free();
            throw new OutOfMemoryException("av_malloc failed for AVIO buffer.");
        }

        var avio = avio_alloc_context(
            buffer,
            IoBufferSize,
            1, // write flag
            (void*)GCHandle.ToIntPtr(_holderHandle),
            null,
            WritePacketCallback,
            null);
        if (avio is null)
        {
            av_free(buffer);
            _holderHandle.Free();
            throw new OutOfMemoryException("avio_alloc_context returned NULL.");
        }

        _avio = avio;
    }

    public AVIOContext* Context
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _avio;
        }
    }

    public long BytesWritten => Volatile.Read(ref _holder.BytesWritten);

    private static int WritePacket(void* opaque, byte* buf, int bufSize)
    {
        var holder = (Holder)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
        try
        {
            if (bufSize > 0)
            {
                var copy = new byte[bufSize];
                new ReadOnlySpan<byte>(buf, bufSize).CopyTo(copy);
                holder.OnBytes(copy);
                Interlocked.Add(ref holder.BytesWritten, bufSize);
            }

            return bufSize;
        }
        catch
        {
            return -1; // muxer surfaces the write failure; the session faults this sink
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_avio is not null)
        {
            // avio_context_free does not free the internal buffer for custom contexts - do both.
            var a = _avio;
            av_free(a->buffer);
            avio_context_free(&a);
        }

        if (_holderHandle.IsAllocated)
            _holderHandle.Free();
    }

    private sealed class Holder(Action<ReadOnlyMemory<byte>> onBytes)
    {
        public readonly Action<ReadOnlyMemory<byte>> OnBytes = onBytes;
        public long BytesWritten;
    }
}

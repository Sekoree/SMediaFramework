using System.Runtime.InteropServices;
using static FFmpeg.AutoGen.ffmpeg;

namespace S.Media.FFmpeg.Internal;

/// <summary>
/// Bridges a managed <see cref="Stream"/> to libavformat via <c>AVIOContext</c> (no temp-file spool).
/// The caller owns <see cref="Stream"/>; this type does not dispose it.
/// </summary>
internal sealed unsafe class StreamAvioBridge : IDisposable
{
    private const int IoBufferSize = 32 * 1024;

    private static readonly avio_alloc_context_read_packet ReadPacketCallback = ReadPacket;
    private static readonly avio_alloc_context_seek SeekCallback = Seek;

    private readonly GCHandle _holderHandle;
    private readonly byte* _buffer;
    private readonly AVIOContext* _avio;
    private bool _disposed;

    private StreamAvioBridge(GCHandle holderHandle, byte* buffer, AVIOContext* avio)
    {
        _holderHandle = holderHandle;
        _buffer = buffer;
        _avio = avio;
    }

    public static StreamAvioBridge Create(Stream stream, bool isSeekable)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("stream must be readable.", nameof(stream));

        var holder = new StreamIoHolder(stream, isSeekable && stream.CanSeek);
        var handle = GCHandle.Alloc(holder);
        byte* buffer = null;
        AVIOContext* avio = null;
        try
        {
            buffer = (byte*)av_malloc((ulong)IoBufferSize);
            if (buffer == null)
                throw new OutOfMemoryException("av_malloc failed for AVIO buffer.");

            var opaque = (void*)GCHandle.ToIntPtr(handle);
            avio = avio_alloc_context(
                buffer,
                IoBufferSize,
                0,
                opaque,
                ReadPacketCallback,
                null,
                holder.IsSeekable ? SeekCallback : null);
            if (avio == null)
                throw new OutOfMemoryException("avio_alloc_context returned NULL.");

            return new StreamAvioBridge(handle, buffer, avio);
        }
        catch
        {
            if (avio != null)
            {
                var a = avio;
                avio_context_free(&a);
            }
            else if (buffer != null)
                av_free(buffer);

            if (handle.IsAllocated)
                handle.Free();
            throw;
        }
    }

    /// <summary>Allocates an <see cref="AVFormatContext"/> and runs probe; caller must detach <c>pb</c> before <c>avformat_close_input</c>, then dispose this bridge.</summary>
    public AVFormatContext* OpenFormatContext(string? probeHintName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        AVFormatContext* fmt = null;
        try
        {
            fmt = avformat_alloc_context();
            if (fmt == null)
                throw new OutOfMemoryException("avformat_alloc_context returned NULL.");

            fmt->pb = _avio;
            fmt->flags |= AVFMT_FLAG_CUSTOM_IO;

            var url = string.IsNullOrEmpty(probeHintName) ? "" : probeHintName;
            var ret = avformat_open_input(&fmt, url, null, null);
            FFmpegException.ThrowIfError(ret, nameof(avformat_open_input));

            var infoRet = avformat_find_stream_info(fmt, null);
            FFmpegException.ThrowIfError(infoRet, nameof(avformat_find_stream_info));
            return fmt;
        }
        catch
        {
            if (fmt != null)
            {
                var f = fmt;
                f->pb = null;
                avformat_close_input(&f);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_avio != null)
        {
            var a = _avio;
            avio_context_free(&a);
        }
        else if (_buffer != null)
        {
            av_free(_buffer);
        }

        if (_holderHandle.IsAllocated)
            _holderHandle.Free();
    }

    private static int ReadPacket(void* opaque, byte* buf, int bufSize)
    {
        var holder = (StreamIoHolder)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
        try
        {
            var total = 0;
            while (total < bufSize)
            {
                var read = holder.Stream.Read(new Span<byte>(buf + total, bufSize - total));
                if (read == 0)
                    break;
                total += read;
            }

            return total == 0 ? AVERROR_EOF : total;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static long Seek(void* opaque, long offset, int whence)
    {
        var holder = (StreamIoHolder)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
        if (!holder.IsSeekable)
            return -1;

        var stream = holder.Stream;
        try
        {
            return whence switch
            {
                AVSEEK_SIZE => stream.Length,
                0 => stream.Seek(offset, SeekOrigin.Begin) >= 0 ? stream.Position : -1,
                1 => stream.Seek(offset, SeekOrigin.Current) >= 0 ? stream.Position : -1,
                2 => stream.Seek(offset, SeekOrigin.End) >= 0 ? stream.Position : -1,
                _ => -1,
            };
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private sealed class StreamIoHolder(Stream stream, bool isSeekable)
    {
        public Stream Stream { get; } = stream;
        public bool IsSeekable { get; } = isSeekable;
    }
}

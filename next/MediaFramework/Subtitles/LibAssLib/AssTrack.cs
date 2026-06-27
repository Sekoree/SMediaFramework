namespace LibAssLib;

/// <summary>
/// Managed wrapper over an <c>ASS_Track</c> — the parsed styles plus timed events. Build it from a whole document
/// (<see cref="AssLibrary.ReadMemory"/>) or incrementally: <see cref="ProcessCodecPrivate"/> for the header blob
/// (the demuxer's codec-private: <c>[Script Info]</c> + <c>[V4+ Styles]</c> + the events <c>Format:</c> line),
/// then <see cref="ProcessChunk"/> per demuxed event.
/// </summary>
public sealed unsafe class AssTrack : IDisposable
{
    private nint _handle;

    internal AssTrack(AssLibrary library)
    {
        _handle = LibAssNative.ass_new_track(library.Handle);
        if (_handle == 0)
            throw new InvalidOperationException("ass_new_track failed.");
    }

    internal AssTrack(nint handle) => _handle = handle;

    internal nint Handle => _handle;

    /// <summary>Feed the codec-private header blob (everything before the events, including the events Format line).</summary>
    public void ProcessCodecPrivate(ReadOnlySpan<byte> header)
    {
        ThrowIfDisposed();
        fixed (byte* d = header)
            LibAssNative.ass_process_codec_private(_handle, d, header.Length);
    }

    /// <summary>Feed event lines in ASS file form (<c>Dialogue: …</c> rows).</summary>
    public void ProcessData(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        fixed (byte* d = data)
            LibAssNative.ass_process_data(_handle, d, data.Length);
    }

    /// <summary>Feed one demuxed event chunk (the event body without timing) with its timecode + duration in ms.</summary>
    public void ProcessChunk(ReadOnlySpan<byte> eventBody, long timecodeMs, long durationMs)
    {
        ThrowIfDisposed();
        fixed (byte* d = eventBody)
            LibAssNative.ass_process_chunk(_handle, d, eventBody.Length, timecodeMs, durationMs);
    }

    /// <summary>Drop all buffered events (e.g. after a seek).</summary>
    public void FlushEvents()
    {
        ThrowIfDisposed();
        LibAssNative.ass_flush_events(_handle);
    }

    public void Dispose()
    {
        if (_handle == 0)
            return;
        LibAssNative.ass_free_track(_handle);
        _handle = 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle == 0, this);
}

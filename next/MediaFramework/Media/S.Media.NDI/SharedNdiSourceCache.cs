using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// Shares one ref-counted <see cref="NDISource"/> per source name across the provider's separate
/// <c>OpenVideo</c>/<c>OpenAudio</c> calls, so an <c>ndi://</c> open uses ONE receiver delivering both audio
/// and video — anchored together on the single audio-driven ingest clock — instead of two independently
/// anchored receivers (the ~startup A/V offset <c>NDIAVCorrelationProbe</c> measured). All consumers of the
/// same sender share the connection (the correct NDI model); the receiver is torn down when the last leased
/// adapter is disposed.
/// </summary>
internal sealed class SharedNdiSourceCache(Func<string, NDISource> open)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public IVideoSource LeaseVideo(string name) => new VideoLease(Acquire(name));

    public IAudioSource LeaseAudio(string name) => new AudioLease(Acquire(name));

    private Entry Acquire(string name)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                // Opened under the lock so two concurrent acquires of the same name can't race into two
                // receivers. NDI opens are infrequent (per cue/clip); the source receives both A and V.
                entry = new Entry(name, open(name), this);
                _entries[name] = entry;
            }

            entry.RefCount++;
            return entry;
        }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.RefCount > 0)
                return;
            _entries.Remove(entry.Name);
        }

        // Outside the lock — receiver teardown stops a capture thread and can block.
        entry.Source.Dispose();
    }

    private sealed class Entry(string name, NDISource source, SharedNdiSourceCache owner)
    {
        public string Name { get; } = name;
        public NDISource Source { get; } = source;
        public int RefCount;

        public void Release() => owner.Release(this);
    }

    /// <summary>A leased view of the shared source's video adapter — delegates everything, and on dispose
    /// releases its reference (the receiver is torn down only when the last lease is disposed).</summary>
    private sealed class VideoLease(Entry entry) : ILiveVideoSource, IDisposable
    {
        private int _disposed;
        private ILiveVideoSource Inner => (ILiveVideoSource)entry.Source.Video;

        public VideoFormat Format => Inner.Format;
        public IReadOnlyList<PixelFormat> NativePixelFormats => Inner.NativePixelFormats;
        public bool IsExhausted => Inner.IsExhausted;
        public void SelectOutputFormat(PixelFormat format) => Inner.SelectOutputFormat(format);
        public bool TryReadNextFrame(out VideoFrame frame) => Inner.TryReadNextFrame(out frame);
        public void RebaseToLatest(TimeSpan playClockNow) => Inner.RebaseToLatest(playClockNow);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                entry.Release();
        }
    }

    /// <summary>A leased view of the shared source's audio adapter (see <see cref="VideoLease"/>).</summary>
    private sealed class AudioLease(Entry entry) : IAudioSource, IDisposable
    {
        private int _disposed;
        private IAudioSource Inner => entry.Source.Audio;

        public AudioFormat Format => Inner.Format;
        public bool IsExhausted => Inner.IsExhausted;
        public bool TryReadNextFrame(out AudioFrame frame) => Inner.TryReadNextFrame(out frame);
        public int ReadInto(Span<float> destination) => Inner.ReadInto(destination);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                entry.Release();
        }
    }
}

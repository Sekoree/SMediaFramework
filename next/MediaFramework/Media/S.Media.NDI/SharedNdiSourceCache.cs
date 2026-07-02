using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.NDI;

/// <summary>
/// Shares one ref-counted <see cref="NDISource"/> across the provider's paired
/// <c>OpenVideo</c>/<c>OpenAudio</c> calls, so an <c>ndi://</c> open uses ONE receiver delivering both audio
/// and video — anchored together on the single audio-driven ingest clock — instead of two independently
/// anchored receivers (the ~startup A/V offset <c>NDIAVCorrelationProbe</c> measured). Independent consumers
/// get independent receivers; otherwise their reads/rebases would mutate the same queues and steal frames.
/// The paired receiver is torn down when its last leased adapter is disposed.
/// </summary>
internal sealed class SharedNdiSourceCache(Func<string, NDISource> open)
{
    private const long PairWindowMilliseconds = 2_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _entries = new(StringComparer.Ordinal);

    public IVideoSource LeaseVideo(string sourceKey) => new VideoLease(AcquireVideo(sourceKey));

    public IAudioSource LeaseAudio(string sourceKey) => new AudioLease(AcquireAudio(sourceKey));

    private Entry AcquireVideo(string name)
    {
        lock (_gate)
        {
            var entry = AddEntryLocked(name);
            entry.RefCount++;
            entry.PendingAudioPairThreadId = Environment.CurrentManagedThreadId;
            entry.PendingAudioPairExpiresAtMs = Environment.TickCount64 + PairWindowMilliseconds;
            return entry;
        }
    }

    private Entry AcquireAudio(string name)
    {
        lock (_gate)
        {
            var entry = FindPendingAudioPairLocked(name) ?? AddEntryLocked(name);
            entry.RefCount++;
            entry.PendingAudioPairThreadId = null;
            entry.PendingAudioPairExpiresAtMs = 0;
            return entry;
        }
    }

    private Entry AddEntryLocked(string name)
    {
        // Opened under the lock so the paired audio acquire cannot miss the just-created receiver. NDI opens
        // are infrequent (per cue/clip), and the source receives both A and V for correlated timeline anchoring.
        var entry = new Entry(name, open(name), this);
        if (!_entries.TryGetValue(name, out var list))
        {
            list = [];
            _entries[name] = list;
        }

        list.Add(entry);
        return entry;
    }

    private Entry? FindPendingAudioPairLocked(string name)
    {
        if (!_entries.TryGetValue(name, out var list))
            return null;

        var threadId = Environment.CurrentManagedThreadId;
        var now = Environment.TickCount64;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var entry = list[i];
            if (entry.PendingAudioPairThreadId == threadId && now <= entry.PendingAudioPairExpiresAtMs)
                return entry;
        }

        return null;
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.RefCount > 0)
                return;
            if (_entries.TryGetValue(entry.Name, out var list))
            {
                list.Remove(entry);
                if (list.Count == 0)
                    _entries.Remove(entry.Name);
            }
        }

        // Outside the lock — receiver teardown stops a capture thread and can block.
        entry.Source.Dispose();
    }

    private sealed class Entry(string name, NDISource source, SharedNdiSourceCache owner)
    {
        public string Name { get; } = name;
        public NDISource Source { get; } = source;
        public int RefCount;
        public int? PendingAudioPairThreadId;
        public long PendingAudioPairExpiresAtMs;

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

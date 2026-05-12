using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public class AudioPrefillTests
{
    [Fact]
    public void PumpWhile_StopsOnZeroRead()
    {
        var fmt = new AudioFormat(48000, 2);
        var src = new ExhaustedAfterOneChunkSource(fmt, floatsPerCall: 4);
        var count = 0;
        AudioPrefill.PumpWhile(
            src,
            _ => count++,
            () => true,
            TimeSpan.FromSeconds(1));
        Assert.Equal(1, count);
    }

    private sealed class ExhaustedAfterOneChunkSource : IAudioSource
    {
        private readonly int _n;
        private bool _done;

        public ExhaustedAfterOneChunkSource(AudioFormat format, int floatsPerCall)
        {
            Format = format;
            _n = floatsPerCall;
        }

        public AudioFormat Format { get; }
        public bool IsExhausted => _done;

        public int ReadInto(Span<float> destination)
        {
            if (_done) return 0;
            for (var i = 0; i < _n && i < destination.Length; i++)
                destination[i] = 0.5f;
            _done = true;
            return Math.Min(_n, destination.Length);
        }
    }
}

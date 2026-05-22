using S.Media.Core;
using S.Media.Core.Audio;
using Xunit;

namespace S.Media.FFmpeg.Tests.Audio;

public sealed class AudioFileDecoderArrayPoolTests
{
    [Fact]
    public void AudioFrame_Release_NullByDefault_DisposeNoOp()
    {
        var frame = new AudioFrame(TimeSpan.Zero, new AudioFormat(48000, 2), 480, new float[960]);
        frame.Dispose(); // no callback; should be safe
        frame.Dispose(); // idempotent
    }

    [Fact]
    public void AudioFrame_Release_InvokedOnDispose()
    {
        var calls = 0;
        var frame = new AudioFrame(TimeSpan.Zero, new AudioFormat(48000, 2), 480, new float[960],
            Release: DisposableRelease.Wrap(() => calls++));
        frame.Dispose();
        Assert.Equal(1, calls);
    }
}

using S.Media.Core.Audio;
using S.Media.PortAudio;
using Xunit;

namespace S.Media.PortAudio.Tests;

public class PortAudioInputTests
{
    private static readonly AudioFormat MonoFormat = new(48000, 1);

    private static bool HasInputDevice()
    {
        PortAudioRuntime.Acquire();
        try { return PortAudioRuntime.DefaultInputDevice >= 0; }
        finally { PortAudioRuntime.Release(); }
    }

    [Fact]
    public void Construct_WithoutStart_DoesNotOpenStream()
    {
        if (!HasInputDevice()) return;

        using var input = new PortAudioInput(MonoFormat);
        Assert.False(input.IsRunning);
        Assert.Equal(MonoFormat, input.Format);
        Assert.Equal(0, input.AvailableSamples);
        Assert.Equal(-1, input.StreamActive);
        Assert.False(input.IsAdvancing);
        Assert.False(input.CheckStreamActive());
        Assert.False(input.StreamInactiveDetected);
        Assert.False(input.CallbackFaulted);
        Assert.Null(input.CallbackFaultException);
        Assert.False(input.HasInputFault);
    }

    [Fact]
    public void TryReadFrame_EmptyBuffer_ReturnsFalse()
    {
        if (!HasInputDevice()) return;

        using var input = new PortAudioInput(MonoFormat);
        Assert.False(input.TryReadFrame(100, out _));
    }

    [Fact]
    public void TryReadFrame_NegativeOrZero_Throws()
    {
        if (!HasInputDevice()) return;

        using var input = new PortAudioInput(MonoFormat);
        Assert.Throws<ArgumentOutOfRangeException>(() => input.TryReadFrame(0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => input.TryReadFrame(-1, out _));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        if (!HasInputDevice()) return;

        var input = new PortAudioInput(MonoFormat);
        input.Dispose();
        input.Dispose();
        Assert.Throws<ObjectDisposedException>(() => input.TryReadFrame(10, out _));
    }

    [Fact]
    public void Start_CapturesAndStop_LifecycleClean()
    {
        if (!HasInputDevice()) return;

        using var input = new PortAudioInput(MonoFormat);
        input.Start();
        Assert.True(input.IsRunning);
        Assert.True(input.StreamActive >= 0);
        Assert.False(input.CallbackFaulted);
        Assert.Null(input.CallbackFaultException);
        Assert.False(input.HasInputFault);
        Assert.True(input.CheckStreamActive());

        // Wait briefly so the callback can deliver a few buffers.
        Thread.Sleep(150);
        Assert.False(input.CallbackFaulted);
        Assert.Null(input.CallbackFaultException);

        // Don't assert that we got data — many CI / no-mic systems will deliver silence
        // or nothing — but the lifecycle should still complete cleanly.
        input.Stop();
        Assert.False(input.IsRunning);
    }
}

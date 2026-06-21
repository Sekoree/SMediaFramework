using System.Globalization;
using System.Linq;
using S.Media.Core.Audio;
using S.Media.PortAudio;
using Xunit;

namespace S.Media.PortAudio.Tests;

public sealed class PortAudioBackendTests
{
    private static bool HasOutputDevice()
    {
        PortAudioRuntime.Acquire();
        try { return PortAudioRuntime.DefaultOutputDevice >= 0; }
        finally { PortAudioRuntime.Release(); }
    }

    [Fact]
    public void Name_IsPortAudio()
    {
        Assert.Equal("PortAudio", new PortAudioBackend().Name);
    }

    [Fact]
    public void EnumerateOutputDevices_DoesNotThrow_AndIdsAreNumericIndices()
    {
        PortAudioRuntime.Acquire();
        try
        {
            var devices = new PortAudioBackend().EnumerateOutputDevices();
            // Every device id round-trips as an invariant integer index (what CreateOutput expects back).
            Assert.All(devices, d =>
                Assert.True(int.TryParse(d.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)));
        }
        finally
        {
            PortAudioRuntime.Release();
        }
    }

    [Fact]
    public void CreateOutput_DefaultDevice_OpensAndDisposes()
    {
        if (!HasOutputDevice()) return; // headless / no device — skip the hardware-touching assertion

        PortAudioRuntime.Acquire();
        try
        {
            var backend = new PortAudioBackend();
            // null deviceId = system default; backend returns a started IAudioOutput.
            var output = backend.CreateOutput(deviceId: null, new AudioFormat(48000, 2));
            using (output as IDisposable)
            {
                Assert.Equal(48000, output.Format.SampleRate);
                Assert.Equal(2, output.Format.Channels);
                var pa = Assert.IsType<PortAudioOutput>(output);
                Assert.True(pa.IsRunning); // CreateOutput returns a ready/started device
            }
        }
        finally
        {
            PortAudioRuntime.Release();
        }
    }
}

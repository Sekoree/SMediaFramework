using HaViz.Desktop.Playback;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;

namespace HaViz.Desktop.Capture;

/// <summary>
/// Line-in/interface capture -> engine PCM: opens the PortAudio input at the device's FULL channel
/// count so any lane is reachable, then gathers only the selected channels (ChannelMap - e.g.
/// inputs 3+4 of a 16-in interface) before handing the interleaved result to the sink. Start/Stop
/// are UI-thread only; the pump runs on a background thread and the sink must be thread-safe.
/// </summary>
public sealed class LineInCapture : IDisposable
{
    private Thread? _thread;
    private volatile bool _stopRequested;

    public bool IsCapturing => _thread is not null;

    /// <summary>Raised on the PUMP thread when capture dies unexpectedly (device unplugged,
    /// stream fault). Not raised for user-requested Stop.</summary>
    public event Action<string>? Faulted;

    public static IReadOnlyList<PortAudioInputDeviceEntry> EnumerateDevices()
    {
        try
        {
            return PortAudioDeviceCatalog.EnumerateInputDevices();
        }
        catch (Exception)
        {
            return []; // PortAudio native missing - the UI shows "no input devices"
        }
    }

    public void Start(PortAudioInputDeviceEntry device, int[] channelsZeroBased, PcmSubmit sink)
    {
        if (_thread is not null)
            return;
        if (channelsZeroBased.Length == 0)
            throw new ArgumentException("select at least one input channel", nameof(channelsZeroBased));

        var rate = device.DefaultSampleRate > 0 ? (int)device.DefaultSampleRate : 48_000;
        var openChannels = Math.Max(device.MaxInputChannels, channelsZeroBased.Max() + 1);
        var input = new PortAudioInput(new AudioFormat(rate, openChannels), device.GlobalDeviceIndex);
        // Construction only opens the stream - without Start() the callback never runs and
        // ReadInto silently returns 0 forever (the backend's CreateInput starts it for you).
        try
        {
            input.Start();
        }
        catch
        {
            input.Dispose();
            throw;
        }

        var map = new ChannelMap(channelsZeroBased);

        _stopRequested = false;
        _thread = new Thread(() => Pump(input, map, openChannels, channelsZeroBased.Length, rate, sink))
        {
            IsBackground = true,
            Name = "haviz-linein",
        };
        _thread.Start();
    }

    private void Pump(PortAudioInput input, ChannelMap map, int srcChannels, int outChannels, int rate, PcmSubmit sink)
    {
        const int framesPerPull = 512;
        var src = new float[framesPerPull * srcChannels];
        var dst = new float[framesPerPull * outChannels];
        try
        {
            while (!_stopRequested)
            {
                var got = input.ReadInto(src); // frame-aligned by contract
                if (got <= 0)
                {
                    Thread.Sleep(2);
                    continue;
                }

                var frames = got / srcChannels;
                map.Apply(src.AsSpan(0, got), srcChannels, dst, frames);
                sink(dst.AsSpan(0, frames * outChannels), rate, outChannels);
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex.Message);
        }
        finally
        {
            input.Dispose();
        }
    }

    public void Stop()
    {
        if (_thread is not { } thread)
            return;
        _stopRequested = true;
        thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();
}

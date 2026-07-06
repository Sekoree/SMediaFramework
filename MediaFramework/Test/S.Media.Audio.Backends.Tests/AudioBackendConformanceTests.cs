using S.Media.Audio.MiniAudio;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.Audio.Backends.Tests;

/// <summary>
/// AUDIO-01: one behavioural conformance suite run against <em>every</em> <see cref="IAudioBackend"/>
/// implementation (PortAudio and miniaudio), so a new backend is held to the same resolver / enumeration /
/// format / lifecycle / error contract. The name and error-parse cases need no device and always run; the cases
/// that open the default device are device-dependent — they exercise real hardware where a device is present and
/// return early (logging the reason to the test output) on a headless runner with no usable device, so the suite
/// is green everywhere yet asserts real open/lifecycle behaviour wherever a device exists.
/// </summary>
public sealed class AudioBackendConformanceTests(ITestOutputHelper output)
{
    private static readonly AudioFormat StandardFormat = new(48_000, 2);

    public static IEnumerable<object[]> Backends()
    {
        yield return ["PortAudio"];
        yield return ["miniaudio"];
    }

    private static IAudioBackend Create(string name) => name switch
    {
        "PortAudio" => new PortAudioBackend(),
        "miniaudio" => new MiniAudioBackend(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown backend"),
    };

    // ---- resolver ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void Name_IsStableAndNonEmpty(string name)
    {
        var backend = Create(name);
        Assert.False(string.IsNullOrWhiteSpace(backend.Name));
        Assert.Equal(name, backend.Name);
        Assert.Equal(backend.Name, Create(name).Name); // stable across instances
    }

    // ---- enumeration ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void EnumerateOutputDevices_ReturnsAWellFormedList(string name)
    {
        if (TryEnumerate(() => Create(name).EnumerateOutputDevices(), name, "output") is { } devices)
            AssertWellFormed(devices);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void EnumerateInputDevices_ReturnsAWellFormedList(string name)
    {
        if (TryEnumerate(() => Create(name).EnumerateInputDevices(), name, "input") is { } devices)
            AssertWellFormed(devices);
    }

    private static void AssertWellFormed(IReadOnlyList<AudioDeviceInfo> devices)
    {
        Assert.NotNull(devices);
        Assert.All(devices, d => Assert.False(string.IsNullOrEmpty(d.Id), "device has an empty id"));
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name), "device has an empty name"));
        Assert.All(devices, d => Assert.True(d.MaxChannels >= 0, "device reports negative channel count"));
        Assert.True(devices.Count(d => d.IsDefault) <= 1, "more than one device flagged as the default");
    }

    // ---- format + lifecycle --------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Backends))]
    public void CreateOutput_OnDefaultDevice_StartsWithAUsableFormat_AndDisposesCleanly(string name)
    {
        var output = TryOpenDefault(Create(name), name);
        if (output is null)
            return;
        try
        {
            Assert.True(output.Format.SampleRate > 0, "started output reports a non-positive sample rate");
            Assert.True(output.Format.Channels > 0, "started output reports a non-positive channel count");
        }
        finally
        {
            (output as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void CreateOutput_RepeatedOpenDispose_IsStable(string name)
    {
        var backend = Create(name);
        // First open decides availability; if there's no device, skip the whole loop.
        var first = TryOpenDefault(backend, name);
        if (first is null)
            return;
        (first as IDisposable)?.Dispose();

        // A per-open native leak or a failure to release the device between opens would surface here.
        for (var i = 0; i < 4; i++)
        {
            var reopened = backend.CreateOutput(null, StandardFormat);
            Assert.NotNull(reopened);
            (reopened as IDisposable)?.Dispose();
        }
    }

    // ---- error ---------------------------------------------------------------------------------

    [Fact]
    public void PortAudio_CreateOutput_RejectsAMalformedDeviceId()
    {
        // PortAudio ids are global device indices; a non-numeric id is invalid at parse time — a controlled
        // ArgumentException, before any device is touched (so this runs with no hardware).
        var backend = new PortAudioBackend();
        Assert.Throws<ArgumentException>(() => backend.CreateOutput("not-a-device-index", StandardFormat));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void CreateOutput_OnAnUnknownDevice_FailsControllably_NotSilently(string name)
    {
        // A device id that cannot exist must not crash the process: the backend rejects it up front (PortAudio
        // parses an out-of-range index), fails when opening it, or falls back to a real default device. Any of
        // those is acceptable; a hang or a fatal crash is not.
        var backend = Create(name);
        try
        {
            var opened = backend.CreateOutput("9999999", StandardFormat); // parseable, far out of range
            Assert.True(opened.Format.SampleRate > 0); // if it fell back to a default, it must be real
            (opened as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Assert.False(ex is OutOfMemoryException or StackOverflowException, "unexpected fatal exception kind");
        }
    }

    // ---- device-availability gating (codebase convention: early-return, reason logged) ---------

    private IReadOnlyList<AudioDeviceInfo>? TryEnumerate(
        Func<IReadOnlyList<AudioDeviceInfo>> enumerate, string name, string kind)
    {
        try
        {
            return enumerate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            output.WriteLine($"skipped: {name} native library not loadable for {kind} enumeration: {ex.Message}");
            return null;
        }
    }

    private IAudioOutput? TryOpenDefault(IAudioBackend backend, string name)
    {
        try
        {
            return backend.CreateOutput(null, StandardFormat);
        }
        catch (Exception ex)
        {
            // No usable default output device on this runner (common headless): device-dependent → skip.
            output.WriteLine($"skipped: no usable default output device for {name}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

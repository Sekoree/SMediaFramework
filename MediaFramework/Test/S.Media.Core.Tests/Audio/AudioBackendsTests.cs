using System.Linq;
using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioBackendsTests
{
    // AudioBackends is a process-wide registry; these tests use unique backend names so they don't collide
    // with each other or with a real backend (e.g. PortAudio) that another test assembly may have registered.

    [Fact]
    public void Register_MakesBackendDiscoverable_CaseInsensitive()
    {
        var backend = new FakeBackend("FakeBackend_Discoverable");
        AudioBackends.Register(backend);

        Assert.Contains(backend, AudioBackends.All);
        Assert.True(AudioBackends.TryGet("fakebackend_discoverable", out var got));
        Assert.Same(backend, got);
        Assert.NotNull(AudioBackends.Default); // at least one backend is registered now
    }

    [Fact]
    public void Register_SameName_ReplacesInPlace_NoDuplicate()
    {
        const string name = "FakeBackend_Replaced";
        var first = new FakeBackend(name);
        var second = new FakeBackend(name);

        AudioBackends.Register(first);
        AudioBackends.Register(second);

        Assert.True(AudioBackends.TryGet(name, out var got));
        Assert.Same(second, got);
        Assert.Equal(1, AudioBackends.All.Count(b => b is FakeBackend f && f.Name == name));
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsFalse()
    {
        Assert.False(AudioBackends.TryGet("no-such-backend-xyz", out var got));
        Assert.Null(got);
    }

    private sealed class FakeBackend(string name) : IAudioBackend
    {
        public string Name { get; } = name;
        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() => [];
        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];
        public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
        public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }
}

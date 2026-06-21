using System.Diagnostics.CodeAnalysis;

namespace S.Media.Core.Audio;

/// <summary>
/// Process-wide registry of available <see cref="IAudioBackend"/> implementations. Each backend registers
/// itself from its <c>MediaFrameworkRuntime</c> hook (e.g. <c>UsePortAudio()</c> registers the PortAudio
/// backend). Hosts then pick a backend by <see cref="IAudioBackend.Name"/> or take <see cref="Default"/>,
/// keeping the rest of the app backend-agnostic.
/// </summary>
public static class AudioBackends
{
    private static readonly Lock Gate = new();
    private static readonly List<IAudioBackend> Backends = [];

    /// <summary>
    /// Registers <paramref name="backend"/>, replacing any existing backend with the same
    /// <see cref="IAudioBackend.Name"/> (case-insensitive) in place. The first distinct backend registered
    /// becomes <see cref="Default"/>.
    /// </summary>
    public static void Register(IAudioBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (Gate)
        {
            var existing = Backends.FindIndex(b => NameEquals(b.Name, backend.Name));
            if (existing >= 0)
                Backends[existing] = backend;
            else
                Backends.Add(backend);
        }
    }

    /// <summary>All registered backends, in registration order.</summary>
    public static IReadOnlyList<IAudioBackend> All
    {
        get { lock (Gate) return Backends.ToArray(); }
    }

    /// <summary>The first registered backend, or <c>null</c> when none has been registered.</summary>
    public static IAudioBackend? Default
    {
        get { lock (Gate) return Backends.Count > 0 ? Backends[0] : null; }
    }

    /// <summary>Looks up a backend by name (case-insensitive).</summary>
    public static bool TryGet(string name, [NotNullWhen(true)] out IAudioBackend? backend)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (Gate)
            backend = Backends.Find(b => NameEquals(b.Name, name));
        return backend is not null;
    }

    private static bool NameEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

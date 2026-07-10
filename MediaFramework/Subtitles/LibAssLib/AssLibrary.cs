using System.Text;

namespace LibAssLib;

/// <summary>
/// Managed wrapper over an <c>ASS_Library</c> - the libass instance that owns font data and mints renderers and
/// tracks. Dispose it <strong>last</strong>, after every <see cref="AssRenderer"/> / <see cref="AssTrack"/> it
/// created.
/// </summary>
public sealed unsafe class AssLibrary : IDisposable
{
    private nint _handle;

    public AssLibrary()
    {
        _handle = LibAssNative.ass_library_init();
        if (_handle == 0)
            throw new InvalidOperationException("ass_library_init failed - is libass available on the search path?");
    }

    internal nint Handle => _handle;

    /// <summary>libass runtime version (BCD, e.g. <c>0x01703000</c> for 1.17.3).</summary>
    public static int Version => LibAssNative.ass_library_version();

    private static readonly Lazy<bool> AvailableProbe = new(() =>
    {
        try
        {
            return LibAssNative.ass_library_version() >= 0;
        }
        catch
        {
            return false; // DllNotFoundException / TypeInitializationException when the native isn't provisioned
        }
    });

    /// <summary>True when native libass can be loaded on this machine (probed once). Lets callers - notably tests on
    /// a runner without the package - skip gracefully instead of throwing <see cref="DllNotFoundException"/>.</summary>
    public static bool IsAvailable => AvailableProbe.Value;

    /// <summary>Enable extraction of fonts embedded in containers (then registered via <see cref="AddFont"/>).</summary>
    public void SetExtractFonts(bool extract)
    {
        ThrowIfDisposed();
        LibAssNative.ass_set_extract_fonts(_handle, extract ? 1 : 0);
    }

    /// <summary>Register a font (e.g. an MKV attachment) so libass can satisfy a style's font-by-name lookup.</summary>
    public void AddFont(string name, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ThrowIfDisposed();
        var nameBytes = Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* n = nameBytes)
        fixed (byte* d = data)
            LibAssNative.ass_add_font(_handle, n, d, data.Length);
    }

    /// <summary>Create an empty track to feed incrementally (header + chunks).</summary>
    public AssTrack CreateTrack()
    {
        ThrowIfDisposed();
        return new AssTrack(this);
    }

    /// <summary>Create a renderer bound to this library.</summary>
    public AssRenderer CreateRenderer()
    {
        ThrowIfDisposed();
        return new AssRenderer(this);
    }

    /// <summary>Parse a complete in-memory ASS/SSA document into a new track.</summary>
    public AssTrack ReadMemory(ReadOnlySpan<byte> document)
    {
        ThrowIfDisposed();
        var copy = document.ToArray(); // ass_read_memory takes a mutable (char*) buffer
        nint track;
        fixed (byte* b = copy)
            track = LibAssNative.ass_read_memory(_handle, b, (nuint)copy.Length, null);
        if (track == 0)
            throw new InvalidOperationException("ass_read_memory failed to parse the document.");
        return new AssTrack(track);
    }

    public void Dispose()
    {
        if (_handle == 0)
            return;
        LibAssNative.ass_library_done(_handle);
        _handle = 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle == 0, this);
}

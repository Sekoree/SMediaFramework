namespace JackLib.Runtime;

/// <summary>
/// Native library name(s) for JACK2.
/// On Linux the installed package provides <c>libjack.so.0</c>; the loader
/// resolves the bare name "libjack" via the standard shared-library search order.
/// On macOS, Homebrew installs <c>libjack.dylib</c> under the JACK2 formula.
/// </summary>
internal static class JackLibraryNames
{
    /// <summary>Default entry used by <see cref="JackLib.Native"/>.</summary>
    public const string Default = "libjack";
}


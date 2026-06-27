using System.Runtime.InteropServices;

namespace LibAssLib;

/// <summary>
/// Direct P/Invoke binding for <b>libass</b> (<c>ass_*</c>) — no custom C shim. Handles are passed as opaque
/// <see cref="nint"/> (<c>ASS_Library*</c>, <c>ASS_Renderer*</c>, <c>ASS_Track*</c>); strings are passed as
/// caller-pinned UTF-8 <c>byte*</c> to avoid marshalling allocations on the render path. The native library is
/// <c>libass.so</c> / <c>ass.dll</c>, deployed by the host alongside its FreeType/FriBidi/HarfBuzz/fontconfig
/// dependencies. The managed wrappers (<see cref="AssLibrary"/>, <see cref="AssRenderer"/>, <see cref="AssTrack"/>)
/// are the supported surface; this type is the raw ABI.
/// </summary>
public static unsafe partial class LibAssNative
{
    private const string Library = "ass";

    // --- ASS_Library -----------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "ass_library_init")]
    public static partial nint ass_library_init();

    [LibraryImport(Library, EntryPoint = "ass_library_done")]
    public static partial void ass_library_done(nint library);

    [LibraryImport(Library, EntryPoint = "ass_set_extract_fonts")]
    public static partial void ass_set_extract_fonts(nint library, int extract);

    [LibraryImport(Library, EntryPoint = "ass_add_font")]
    public static partial void ass_add_font(nint library, byte* name, byte* data, int dataSize);

    [LibraryImport(Library, EntryPoint = "ass_clear_fonts")]
    public static partial void ass_clear_fonts(nint library);

    [LibraryImport(Library, EntryPoint = "ass_library_version")]
    public static partial int ass_library_version();

    // --- ASS_Renderer ----------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "ass_renderer_init")]
    public static partial nint ass_renderer_init(nint library);

    [LibraryImport(Library, EntryPoint = "ass_renderer_done")]
    public static partial void ass_renderer_done(nint renderer);

    [LibraryImport(Library, EntryPoint = "ass_set_frame_size")]
    public static partial void ass_set_frame_size(nint renderer, int w, int h);

    [LibraryImport(Library, EntryPoint = "ass_set_storage_size")]
    public static partial void ass_set_storage_size(nint renderer, int w, int h);

    [LibraryImport(Library, EntryPoint = "ass_set_hinting")]
    public static partial void ass_set_hinting(nint renderer, int hinting);

    [LibraryImport(Library, EntryPoint = "ass_set_fonts")]
    public static partial void ass_set_fonts(
        nint renderer, byte* defaultFont, byte* defaultFamily, int defaultFontProvider, byte* config, int update);

    // --- ASS_Track -------------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "ass_new_track")]
    public static partial nint ass_new_track(nint library);

    [LibraryImport(Library, EntryPoint = "ass_free_track")]
    public static partial void ass_free_track(nint track);

    [LibraryImport(Library, EntryPoint = "ass_read_memory")]
    public static partial nint ass_read_memory(nint library, byte* buf, nuint bufSize, byte* codepage);

    [LibraryImport(Library, EntryPoint = "ass_process_codec_private")]
    public static partial void ass_process_codec_private(nint track, byte* data, int size);

    [LibraryImport(Library, EntryPoint = "ass_process_data")]
    public static partial void ass_process_data(nint track, byte* data, int size);

    [LibraryImport(Library, EntryPoint = "ass_process_chunk")]
    public static partial void ass_process_chunk(nint track, byte* data, int size, long timecode, long duration);

    [LibraryImport(Library, EntryPoint = "ass_flush_events")]
    public static partial void ass_flush_events(nint track);

    // --- rendering -------------------------------------------------------------------------------

    /// <summary>Renders the track at <paramref name="now"/> ms; returns the head <see cref="AssImage"/>* (or 0).
    /// <paramref name="detectChange"/> may be null; otherwise it receives 0/1/2 (unchanged/positions/content).</summary>
    [LibraryImport(Library, EntryPoint = "ass_render_frame")]
    public static partial AssImage* ass_render_frame(nint renderer, nint track, long now, int* detectChange);
}

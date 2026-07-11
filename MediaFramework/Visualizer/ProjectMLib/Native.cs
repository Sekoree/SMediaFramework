using System.Runtime.InteropServices;
using ProjectMLib.Runtime;

namespace ProjectMLib;

/// <summary>projectM 4.x channel layout for PCM ingestion (audio.h).</summary>
internal enum ProjectMChannels : uint
{
    Mono = 1,
    Stereo = 2,
}

/// <summary>
/// Raw P/Invoke surface of libprojectM-4 (the subset the visualizer uses). All functions require a
/// CURRENT OpenGL context on the calling thread for create/render; see the projectM C API docs.
/// </summary>
internal static unsafe partial class Native
{
    private const string LibraryName = ProjectMLibraryNames.Default;

    // core.h
    [LibraryImport(LibraryName)]
    internal static partial nint projectm_create();

    [LibraryImport(LibraryName)]
    internal static partial void projectm_destroy(nint instance);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void projectm_load_preset_file(
        nint instance, string filename, [MarshalAs(UnmanagedType.I1)] bool smoothTransition);

    [LibraryImport(LibraryName)]
    internal static partial void projectm_get_version_components(int* major, int* minor, int* patch);

    // render_opengl.h
    [LibraryImport(LibraryName)]
    internal static partial void projectm_opengl_render_frame(nint instance);

    // parameters.h
    [LibraryImport(LibraryName)]
    internal static partial void projectm_set_window_size(nint instance, nuint width, nuint height);

    [LibraryImport(LibraryName)]
    internal static partial void projectm_set_fps(nint instance, int fps);

    [LibraryImport(LibraryName)]
    internal static partial void projectm_set_preset_duration(nint instance, double seconds);

    [LibraryImport(LibraryName)]
    internal static partial void projectm_set_soft_cut_duration(nint instance, double seconds);

    [LibraryImport(LibraryName)]
    internal static partial void projectm_set_beat_sensitivity(nint instance, float sensitivity);

    [LibraryImport(LibraryName)]
    internal static partial void projectm_set_aspect_correction(
        nint instance, [MarshalAs(UnmanagedType.I1)] bool enabled);

    [LibraryImport(LibraryName)]
    internal static partial void projectm_set_preset_locked(
        nint instance, [MarshalAs(UnmanagedType.I1)] bool locked);

    // audio.h
    [LibraryImport(LibraryName)]
    internal static partial uint projectm_pcm_get_max_samples();

    [LibraryImport(LibraryName)]
    internal static partial void projectm_pcm_add_float(
        nint instance, float* samples, uint count, ProjectMChannels channels);
}

using System.Text;

namespace LibAssLib;

/// <summary>
/// Managed wrapper over an <c>ASS_Renderer</c> — renders a track to <see cref="AssImage"/> alpha-bitmap layers at
/// a given frame size and time. Configure the frame/storage size and fonts once, then call
/// <see cref="RenderFrame"/> per output frame.
/// </summary>
public sealed unsafe class AssRenderer : IDisposable
{
    private nint _handle;

    internal AssRenderer(AssLibrary library)
    {
        _handle = LibAssNative.ass_renderer_init(library.Handle);
        if (_handle == 0)
            throw new InvalidOperationException("ass_renderer_init failed.");
    }

    /// <summary>Output frame size (pixels) libass renders into.</summary>
    public void SetFrameSize(int width, int height)
    {
        ThrowIfDisposed();
        LibAssNative.ass_set_frame_size(_handle, width, height);
    }

    /// <summary>Source storage size — the resolution events were authored against (a track's PlayResX/Y).</summary>
    public void SetStorageSize(int width, int height)
    {
        ThrowIfDisposed();
        LibAssNative.ass_set_storage_size(_handle, width, height);
    }

    /// <summary>FreeType hinting mode (default <see cref="AssHinting.None"/> is recommended for fidelity).</summary>
    public void SetHinting(AssHinting hinting)
    {
        ThrowIfDisposed();
        LibAssNative.ass_set_hinting(_handle, (int)hinting);
    }

    /// <summary>
    /// Configure the font provider. <paramref name="defaultFamily"/> is the fallback family used when a style's
    /// font is missing; <see cref="AssDefaultFontProvider.Autodetect"/> picks fontconfig / DirectWrite / CoreText
    /// per platform. <paramref name="defaultFontFile"/> optionally forces a specific font file.
    /// </summary>
    public void SetFonts(
        string? defaultFamily = "Sans",
        AssDefaultFontProvider provider = AssDefaultFontProvider.Autodetect,
        string? defaultFontFile = null,
        bool update = true)
    {
        ThrowIfDisposed();
        var familyBytes = defaultFamily is null ? null : Encoding.UTF8.GetBytes(defaultFamily + '\0');
        var fontBytes = defaultFontFile is null ? null : Encoding.UTF8.GetBytes(defaultFontFile + '\0');
        fixed (byte* fam = familyBytes)
        fixed (byte* font = fontBytes)
            LibAssNative.ass_set_fonts(_handle, font, fam, (int)provider, null, update ? 1 : 0);
    }

    /// <summary>Global font-size multiplier (1.0 = the document's own sizing). Clamped to a sane range so an
    /// operator typo can't shrink subtitles to nothing or blow them up off-screen.</summary>
    public void SetFontScale(double fontScale)
    {
        ThrowIfDisposed();
        var clamped = Math.Clamp(fontScale, 0.25, 5.0);
        LibAssNative.ass_set_font_scale(_handle, clamped);
    }

    /// <summary>
    /// Render <paramref name="track"/> at <paramref name="timeMs"/>. Returns the head of the layer list (or
    /// <c>null</c> when nothing shows); <paramref name="changed"/> is true when the image differs from the prior
    /// call. The returned pointer is owned by libass and is only valid until the next <see cref="RenderFrame"/>
    /// or dispose — composite it immediately (see <see cref="AssImageBlender"/>).
    /// </summary>
    public AssImage* RenderFrame(AssTrack track, long timeMs, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(track);
        ThrowIfDisposed();
        var detect = 0;
        var head = LibAssNative.ass_render_frame(_handle, track.Handle, timeMs, &detect);
        changed = detect != 0;
        return head;
    }

    /// <summary>
    /// Renders <paramref name="track"/> at <paramref name="timeMs"/> and composites the result into
    /// <paramref name="bgra"/> (premultiplied BGRA32, <paramref name="stride"/> bytes/row). Returns
    /// <see cref="AssRenderOutcome.Empty"/> when nothing shows (buffer untouched), <see cref="AssRenderOutcome.Unchanged"/>
    /// when the image equals the previous render (buffer untouched — keep your last frame), or
    /// <see cref="AssRenderOutcome.Rendered"/> after clearing + blending. Safe to call per output frame: libass
    /// caches internally and flags unchanged frames so the blend is skipped. This is the allocation-free path —
    /// no pointers escape to the caller.
    /// </summary>
    public AssRenderOutcome RenderInto(AssTrack track, long timeMs, Span<byte> bgra, int width, int height, int stride)
    {
        ThrowIfDisposed();
        if (width <= 0 || height <= 0 || stride < width * 4)
            throw new ArgumentOutOfRangeException(nameof(stride), "stride must be at least width*4 and dimensions positive.");
        if ((long)stride * height > bgra.Length)
            throw new ArgumentException("Destination buffer is smaller than stride × height.", nameof(bgra));

        var head = RenderFrame(track, timeMs, out var changed);
        if (head == null)
            return AssRenderOutcome.Empty;
        if (!changed)
            return AssRenderOutcome.Unchanged;

        bgra.Clear();
        AssImageBlender.Blend(head, bgra, width, height, stride);
        return AssRenderOutcome.Rendered;
    }

    public void Dispose()
    {
        if (_handle == 0)
            return;
        LibAssNative.ass_renderer_done(_handle);
        _handle = 0;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle == 0, this);
}

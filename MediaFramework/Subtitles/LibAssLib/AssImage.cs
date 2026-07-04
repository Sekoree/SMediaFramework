using System.Runtime.InteropServices;

namespace LibAssLib;

/// <summary>
/// Mirror of libass <c>ASS_Image</c> — one alpha-bitmap layer of a rendered subtitle frame. <c>ass_render_frame</c>
/// returns a singly-linked list of these (one per glyph fill / outline / shadow run). <see cref="Bitmap"/> is a
/// 1-byte-per-pixel coverage buffer; <see cref="Color"/> is the fill color and alpha; the layer is placed at
/// (<see cref="DstX"/>, <see cref="DstY"/>) in the target frame. Sequential layout matches the C natural layout
/// (the runtime inserts the same pointer-alignment padding).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct AssImage
{
    /// <summary>Bitmap width in pixels.</summary>
    public int W;

    /// <summary>Bitmap height in pixels.</summary>
    public int H;

    /// <summary>Row stride in bytes. The last row may hold only <see cref="W"/> valid bytes, not a full stride.</summary>
    public int Stride;

    /// <summary>1-byte-per-pixel alpha coverage buffer (0 = transparent, 255 = full coverage).</summary>
    public byte* Bitmap;

    /// <summary>Fill color and alpha as <c>0xRRGGBBAA</c>; <c>AA</c> is transparency (0 = opaque, 255 = clear).</summary>
    public uint Color;

    /// <summary>X placement of this bitmap inside the video frame.</summary>
    public int DstX;

    /// <summary>Y placement of this bitmap inside the video frame.</summary>
    public int DstY;

    /// <summary>Next layer in the linked list, or <c>null</c>.</summary>
    public AssImage* Next;

    /// <summary><see cref="AssImageType"/> — character fill, outline, or shadow.</summary>
    public int Type;
}

/// <summary>libass <c>ASS_Image</c> layer kind (the anonymous enum on <c>type</c>).</summary>
public enum AssImageType
{
    Character = 0,
    Outline = 1,
    Shadow = 2,
}

/// <summary>libass <c>ASS_DefaultFontProvider</c> — the font backend libass loads fonts through.</summary>
public enum AssDefaultFontProvider
{
    None = 0,
    Autodetect = 1,
    CoreText = 2,
    Fontconfig = 3,
    DirectWrite = 4,
}

/// <summary>libass <c>ASS_Hinting</c> — FreeType hinting mode.</summary>
public enum AssHinting
{
    None = 0,
    Light = 1,
    Normal = 2,
    Native = 3,
}

/// <summary>Result of <see cref="AssRenderer.RenderInto"/> — whether (and why) the destination buffer was written.</summary>
public enum AssRenderOutcome
{
    /// <summary>Nothing shows at this time; the buffer was left untouched.</summary>
    Empty,

    /// <summary>The image is identical to the previous render; the buffer was left untouched — reuse it.</summary>
    Unchanged,

    /// <summary>The buffer was cleared and the new image blended into it.</summary>
    Rendered,
}

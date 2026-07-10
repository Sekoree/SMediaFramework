namespace LibAssLib;

/// <summary>
/// Composites a libass <see cref="AssImage"/> layer list onto a <strong>premultiplied BGRA32</strong> buffer -
/// the format the compositor consumes. Each layer is a 1-byte coverage bitmap tinted by its <c>0xRRGGBBAA</c>
/// color (where <c>AA</c> is transparency: 0 opaque, 255 clear), drawn source-over in list order (shadow →
/// outline → fill, which libass already emits back-to-front).
/// </summary>
public static unsafe class AssImageBlender
{
    /// <summary>
    /// Blend <paramref name="head"/>'s layer list onto <paramref name="bgra"/> (a <paramref name="width"/>×
    /// <paramref name="height"/> BGRA32 buffer, <paramref name="stride"/> bytes per row). The buffer is NOT
    /// cleared first - zero it for a transparent overlay. Returns the number of pixels touched.
    /// </summary>
    public static long Blend(AssImage* head, Span<byte> bgra, int width, int height, int stride)
    {
        long touched = 0;
        for (var img = head; img != null; img = img->Next)
        {
            // libass color is 0xRRGGBBAA; AA is transparency, so layer opacity = 255 - AA.
            var color = img->Color;
            int r = (byte)(color >> 24);
            int g = (byte)(color >> 16);
            int b = (byte)(color >> 8);
            var opacity = 255 - (int)(color & 0xFF);
            if (opacity <= 0 || img->W <= 0 || img->H <= 0)
                continue;

            for (var y = 0; y < img->H; y++)
            {
                var dstY = img->DstY + y;
                if (dstY < 0 || dstY >= height)
                    continue;

                var srcRow = img->Bitmap + (nint)y * img->Stride;
                var dstRow = dstY * stride;
                for (var x = 0; x < img->W; x++)
                {
                    var dstX = img->DstX + x;
                    if ((uint)dstX >= (uint)width)
                        continue;

                    // Source alpha = coverage × layer opacity.
                    var a = srcRow[x] * opacity / 255;
                    if (a == 0)
                        continue;

                    var di = dstRow + dstX * 4;
                    var inv = 255 - a;
                    // Premultiplied source-over: out = src·a + dst·(1-a), src color premultiplied by a.
                    bgra[di + 0] = (byte)((b * a + bgra[di + 0] * inv) / 255);
                    bgra[di + 1] = (byte)((g * a + bgra[di + 1] * inv) / 255);
                    bgra[di + 2] = (byte)((r * a + bgra[di + 2] * inv) / 255);
                    bgra[di + 3] = (byte)(a + bgra[di + 3] * inv / 255);
                    touched++;
                }
            }
        }

        return touched;
    }
}

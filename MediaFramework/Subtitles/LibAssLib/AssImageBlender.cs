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
        fixed (byte* pDst = bgra)
        {
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

                // Canvas clip hoisted out of the pixel loop (the old loop re-tested every pixel
                // and every row; animated full-width events run this once per output frame).
                var xStart = Math.Max(0, -img->DstX);
                var xEnd = Math.Min(img->W, width - img->DstX);
                var yStart = Math.Max(0, -img->DstY);
                var yEnd = Math.Min(img->H, height - img->DstY);
                if (xStart >= xEnd || yStart >= yEnd)
                    continue;

                for (var y = yStart; y < yEnd; y++)
                {
                    var srcRow = img->Bitmap + (nint)y * img->Stride;
                    var dstPx = pDst + (nint)(img->DstY + y) * stride + (nint)(img->DstX + xStart) * 4;
                    for (var x = xStart; x < xEnd; x++, dstPx += 4)
                    {
                        // Source alpha = coverage × layer opacity.
                        var a = srcRow[x] * opacity / 255;
                        if (a == 0)
                            continue;

                        var inv = 255 - a;
                        // Premultiplied source-over: out = src·a + dst·(1-a), src color premultiplied by a.
                        dstPx[0] = (byte)((b * a + dstPx[0] * inv) / 255);
                        dstPx[1] = (byte)((g * a + dstPx[1] * inv) / 255);
                        dstPx[2] = (byte)((r * a + dstPx[2] * inv) / 255);
                        dstPx[3] = (byte)(a + dstPx[3] * inv / 255);
                        touched++;
                    }
                }
            }
        }

        return touched;
    }
}

namespace S.Media.OpenGL.Internal;

/// <summary>Computes OpenGL <c>GL_UNPACK_ROW_LENGTH</c> values for padded CPU uploads.</summary>
internal static class OpenGlUnpackRowLength
{
    /// <summary>
    /// Returns the unpack row length in pixels/components, or 0 when the row is tightly packed.
    /// </summary>
    /// <param name="rowPitchBytes">Source row pitch in bytes (e.g. D3D11 padded pitch).</param>
    /// <param name="visiblePixelsPerRow">Visible pixels per row in the upload format.</param>
    /// <param name="bytesPerPixel">Bytes per uploaded pixel/component (1 for R8, 2 for RG).</param>
    public static int Compute(int rowPitchBytes, int visiblePixelsPerRow, int bytesPerPixel = 1)
    {
        if (bytesPerPixel <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerPixel));
        if (rowPitchBytes % bytesPerPixel != 0)
        {
            throw new InvalidOperationException(
                "Row pitch is not aligned to the upload pixel size.");
        }

        var rowLengthPixels = rowPitchBytes / bytesPerPixel;
        return rowLengthPixels == visiblePixelsPerRow ? 0 : rowLengthPixels;
    }
}

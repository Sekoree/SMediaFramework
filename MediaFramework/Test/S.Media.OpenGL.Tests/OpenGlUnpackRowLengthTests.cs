using S.Media.OpenGL.Internal;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class OpenGlUnpackRowLengthTests
{
    [Theory]
    [InlineData(1920, 2048, 1, 2048)]
    [InlineData(1920, 1920, 1, 0)]
    [InlineData(960, 2048, 2, 1024)]
    public void Compute_returns_row_length_in_pixels(int visiblePixels, int rowPitchBytes, int bytesPerPixel, int expected)
    {
        Assert.Equal(expected, OpenGlUnpackRowLength.Compute(rowPitchBytes, visiblePixels, bytesPerPixel));
    }
}

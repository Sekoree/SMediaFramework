using S.Media.Core;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// Generates the calibration grid the mapping editor pushes through a composition: a labeled-ish
/// BGRA frame (grid lines, border, center cross, per-corner color markers for orientation) the
/// operator aligns against physical panels. One-time managed render — performance irrelevant.
/// </summary>
internal static class MappingTestPattern
{
    public static VideoFrame Render(VideoFormat canvas)
    {
        var width = canvas.Width;
        var height = canvas.Height;
        var stride = width * 4;
        var pixels = new byte[stride * height];

        // Background: dark gray so projector black-level is distinguishable from "no section".
        Fill(pixels, stride, 0, 0, width, height, 0x20, 0x20, 0x20);

        // Grid every 1/16 of the larger dimension (≈120 px at 1080p), brighter every 4th line.
        var step = Math.Max(16, Math.Max(width, height) / 16);
        for (int x = 0, i = 0; x < width; x += step, i++)
        {
            var v = (byte)(i % 4 == 0 ? 0xC0 : 0x60);
            Fill(pixels, stride, x, 0, Math.Min(2, width - x), height, v, v, v);
        }
        for (int y = 0, i = 0; y < height; y += step, i++)
        {
            var v = (byte)(i % 4 == 0 ? 0xC0 : 0x60);
            Fill(pixels, stride, 0, y, width, Math.Min(2, height - y), v, v, v);
        }

        // Center cross (red) — quick sanity anchor for section alignment.
        Fill(pixels, stride, width / 2 - 1, 0, 2, height, 0x20, 0x20, 0xE0);
        Fill(pixels, stride, 0, height / 2 - 1, width, 2, 0x20, 0x20, 0xE0);

        // Border (white, 4 px) — edge visibility on every panel.
        Fill(pixels, stride, 0, 0, width, 4, 0xFF, 0xFF, 0xFF);
        Fill(pixels, stride, 0, height - 4, width, 4, 0xFF, 0xFF, 0xFF);
        Fill(pixels, stride, 0, 0, 4, height, 0xFF, 0xFF, 0xFF);
        Fill(pixels, stride, width - 4, 0, 4, height, 0xFF, 0xFF, 0xFF);

        // Corner markers — distinct colors so flipped/rotated sections are obvious:
        // TL white, TR red, BL green, BR blue.
        var m = Math.Max(24, step / 2);
        Fill(pixels, stride, 0, 0, m, m, 0xFF, 0xFF, 0xFF);
        Fill(pixels, stride, width - m, 0, m, m, 0x20, 0x20, 0xE0);
        Fill(pixels, stride, 0, height - m, m, m, 0x20, 0xC0, 0x20);
        Fill(pixels, stride, width - m, height - m, m, m, 0xE0, 0x60, 0x20);

        return new VideoFrame(
            TimeSpan.Zero,
            canvas,
            new ReadOnlyMemory<byte>(pixels),
            stride,
            metadata: new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Straight));
    }

    private static void Fill(byte[] bgra, int stride, int x, int y, int w, int h, byte b, byte g, byte r)
    {
        var height = bgra.Length / stride;
        var width = stride / 4;
        var x1 = Math.Min(width, x + w);
        var y1 = Math.Min(height, y + h);
        for (var py = Math.Max(0, y); py < y1; py++)
        {
            var row = py * stride;
            for (var px = Math.Max(0, x); px < x1; px++)
            {
                var idx = row + px * 4;
                bgra[idx + 0] = b;
                bgra[idx + 1] = g;
                bgra[idx + 2] = r;
                bgra[idx + 3] = 0xFF;
            }
        }
    }
}

using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using LibAssLib;

namespace LibAssLib.Benchmarks;

/// <summary>
/// Measures <see cref="AssImageBlender.Blend"/> — the scalar per-pixel loop that runs once per
/// output frame whenever animated ASS content (karaoke, transforms, scrolls) marks the frame
/// changed. Two shapes: many small glyph runs (typical dialogue) and one full-width banner
/// (worst case). Synthetic <see cref="AssImage"/> lists live in native memory; no libass needed.
/// </summary>
[MemoryDiagnoser]
public unsafe class AssBlendBenchmarks
{
    private const int Width = 1920;
    private const int Height = 1080;

    private byte[] _canvas = null!;
    private AssImage* _glyphList;
    private AssImage* _bannerList;

    [GlobalSetup]
    public void Setup()
    {
        _canvas = new byte[Width * Height * 4];
        _glyphList = BuildList(count: 24, w: 48, h: 64, startX: 200, startY: 900, stepX: 56);
        _bannerList = BuildList(count: 1, w: 1920, h: 200, startX: 0, startY: 860, stepX: 0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        FreeList(_glyphList);
        FreeList(_bannerList);
        _glyphList = null;
        _bannerList = null;
    }

    private static AssImage* BuildList(int count, int w, int h, int startX, int startY, int stepX)
    {
        var images = (AssImage*)NativeMemory.Alloc((nuint)(count * sizeof(AssImage)));
        for (var i = 0; i < count; i++)
        {
            var bitmap = (byte*)NativeMemory.Alloc((nuint)(w * h));
            for (var p = 0; p < w * h; p++)
                bitmap[p] = (byte)(p * 37 % 256);

            images[i] = new AssImage
            {
                W = w,
                H = h,
                Stride = w,
                Bitmap = bitmap,
                Color = 0xFFFFFF20, // white, mostly opaque
                DstX = startX + i * stepX,
                DstY = startY,
                Next = i + 1 < count ? images + i + 1 : null,
                Type = (int)AssImageType.Character,
            };
        }

        return images;
    }

    private static void FreeList(AssImage* head)
    {
        for (var img = head; img != null;)
        {
            NativeMemory.Free(img->Bitmap);
            var next = img->Next;
            img = next;
        }

        // The AssImage structs themselves are one contiguous block starting at the head.
        NativeMemory.Free(head);
    }

    [Benchmark]
    public long BlendGlyphRuns24() => AssImageBlender.Blend(_glyphList, _canvas, Width, Height, Width * 4);

    [Benchmark]
    public long BlendBanner1920x200() => AssImageBlender.Blend(_bannerList, _canvas, Width, Height, Width * 4);
}

using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Diagnostics;

public sealed class MediaFrameworkExtensionRegistryTests
{
    [Fact]
    public void RegisterImageExtension_dispatches_by_extension()
    {
        var ext = ".testimg" + Guid.NewGuid().ToString("N");
        MediaFrameworkExtensionRegistry.RegisterImageExtension(ext, path => new StubImageSource(path));
        Assert.Contains(ext, MediaFrameworkExtensionRegistry.RegisteredImageExtensions);
        var src = VideoSource.OpenImage("folder/file" + ext);
        Assert.IsType<StubImageSource>(src);
    }

    private sealed class StubImageSource(string path) : IVideoSource
    {
        public string Path { get; } = path;
        public VideoFormat Format => new(64, 64, PixelFormat.Bgra32, new Rational(30, 1));
        public IReadOnlyList<PixelFormat> NativePixelFormats => [PixelFormat.Bgra32];
        public bool IsExhausted => true;
        public void SelectOutputFormat(PixelFormat format) { }
        public bool TryReadNextFrame(out VideoFrame frame) => throw new NotSupportedException();
    }
}

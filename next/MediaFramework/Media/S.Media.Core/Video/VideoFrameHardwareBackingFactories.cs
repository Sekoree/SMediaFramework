using S.Media.Core;

namespace S.Media.Core.Video;

/// <summary>Shared helpers for building <see cref="VideoFrame"/> instances from hardware backings.</summary>
internal static class VideoFrameHardwareBackingFactories
{
    internal static VideoFrame CreateHardwareFrame(
        VideoFrameHardwareBacking backing,
        TimeSpan presentationTime,
        VideoFormat format,
        int yPitch,
        int uvPitch,
        VideoFrameMetadata metadata,
        Action? additionalRelease)
    {
        ArgumentNullException.ThrowIfNull(backing);
        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { yPitch, uvPitch };
        var release = additionalRelease is null
            ? backing
            : DisposableRelease.Chain(backing, DisposableRelease.Wrap(additionalRelease));
        return new VideoFrame(presentationTime, format, planes, strides, metadata, backing, release);
    }
}

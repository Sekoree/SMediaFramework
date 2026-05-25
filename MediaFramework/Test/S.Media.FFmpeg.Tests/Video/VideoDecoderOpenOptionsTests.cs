using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public sealed class VideoDecoderOpenOptionsTests
{
    private const string EnvName = "MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY";

    [Fact]
    public void Win32Nv12SharedHandleOnlyExport_defaults_false()
    {
        var o = new VideoDecoderOpenOptions();
        Assert.False(o.Win32Nv12SharedHandleOnlyExport);
    }

    [Fact]
    public void Win32Nv12SharedHandleOnlyExport_can_be_set_true()
    {
        var o = new VideoDecoderOpenOptions { Win32Nv12SharedHandleOnlyExport = true };
        Assert.True(o.Win32Nv12SharedHandleOnlyExport);
    }

    [Fact]
    public void IsWin32Nv12SharedHandleOnlyRequested_false_when_unset()
    {
        var prior = Environment.GetEnvironmentVariable(EnvName);
        try
        {
            Environment.SetEnvironmentVariable(EnvName, null);
            Assert.False(VideoDecoderOpenOptions.IsWin32Nv12SharedHandleOnlyRequested(null));
            Assert.False(VideoDecoderOpenOptions.IsWin32Nv12SharedHandleOnlyRequested(new VideoDecoderOpenOptions()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, prior);
        }
    }

    [Fact]
    public void IsWin32Nv12SharedHandleOnlyRequested_true_from_property()
    {
        var prior = Environment.GetEnvironmentVariable(EnvName);
        try
        {
            Environment.SetEnvironmentVariable(EnvName, null);
            var o = new VideoDecoderOpenOptions { Win32Nv12SharedHandleOnlyExport = true };
            Assert.True(VideoDecoderOpenOptions.IsWin32Nv12SharedHandleOnlyRequested(o));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, prior);
        }
    }

    [Fact]
    public void IsWin32Nv12SharedHandleOnlyRequested_true_from_env_1()
    {
        var prior = Environment.GetEnvironmentVariable(EnvName);
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "1");
            Assert.True(VideoDecoderOpenOptions.IsWin32Nv12SharedHandleOnlyRequested(new VideoDecoderOpenOptions()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, prior);
        }
    }

    [Fact]
    public void IsWin32Nv12SharedHandleOnlyRequested_true_from_env_true_case_insensitive()
    {
        var prior = Environment.GetEnvironmentVariable(EnvName);
        try
        {
            Environment.SetEnvironmentVariable(EnvName, "TRUE");
            Assert.True(VideoDecoderOpenOptions.IsWin32Nv12SharedHandleOnlyRequested(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, prior);
        }
    }
}

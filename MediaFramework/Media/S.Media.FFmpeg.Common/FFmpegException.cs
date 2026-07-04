using System.Runtime.InteropServices;

namespace S.Media.FFmpeg.Common;

public sealed class FFmpegException : Exception
{
    public int ErrorCode { get; }

    public FFmpegException(int errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>For failures that have no libav error code (null returns, missing streams).</summary>
    public FFmpegException(string message) : base(message)
    {
    }

    internal static unsafe void ThrowIfError(int ret, string operation)
    {
        if (ret >= 0) return;

        const int bufSize = 1024;
        var buf = stackalloc byte[bufSize];
        av_strerror(ret, buf, (ulong)bufSize);
        var description = Marshal.PtrToStringAnsi((IntPtr)buf) ?? "unknown error";
        throw new FFmpegException(ret, $"{operation} failed ({ret}): {description}");
    }
}

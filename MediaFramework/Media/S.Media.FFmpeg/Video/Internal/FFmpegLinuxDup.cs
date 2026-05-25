namespace S.Media.FFmpeg.Video.Internal;

internal static class FFmpegLinuxDup
{
    internal static bool IsLinux => OperatingSystem.IsLinux();

    internal static int Dup(int fd)
    {
        var d = dup(fd);
        return d;
    }

    internal static void CloseSilently(int fd)
    {
        if (fd >= 0)
            _ = libc_close(fd);
    }

    [global::System.Runtime.InteropServices.DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int dup(int fd);

    [global::System.Runtime.InteropServices.DllImport("libc", EntryPoint = "close")]
    private static extern int libc_close(int fd);
}

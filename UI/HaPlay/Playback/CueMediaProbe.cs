using S.Media.FFmpeg;

namespace HaPlay.Playback;

internal static class CueMediaProbe
{
    public static async Task<int?> TryProbeDurationMsAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var decoder = await Task.Run(() => MediaContainerDecoder.Open(path)).ConfigureAwait(false);
            try
            {
                var ms = (int)Math.Min(int.MaxValue, decoder.Duration.TotalMilliseconds);
                return ms > 0 ? ms : null;
            }
            finally
            {
                decoder.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }
}

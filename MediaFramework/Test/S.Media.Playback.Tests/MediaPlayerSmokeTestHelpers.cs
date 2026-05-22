using System.Diagnostics;

namespace S.Media.Playback.Tests;

internal static class MediaPlayerSmokeTestHelpers
{
    public static byte[] CreateWavBytes(double durationSeconds = 0.1)
    {
        const int sampleRate = 48_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const double frequency = 440.0;

        var sampleCount = (int)(sampleRate * durationSeconds);
        var dataBytes = sampleCount * channels * (bitsPerSample / 8);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataBytes);

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(2.0 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.25);
            bw.Write(sample);
        }

        return ms.ToArray();
    }

    public static bool TryGenerateAudioVideo(string path, int durationSec = 5)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y",
                    "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={durationSec}",
                    "-f", "lavfi", "-i", $"testsrc=size=320x240:rate=10:duration={durationSec}",
                    "-shortest",
                    "-c:a", "aac",
                    "-c:v", "libx264",
                    "-g", "10",
                    "-keyint_min", "10",
                    "-pix_fmt", "yuv420p",
                    "-loglevel", "error",
                    path,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(30_000);
            return p.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void WriteMinimalPng(string path)
    {
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        File.WriteAllBytes(path, png);
    }

    public static int CountTempSpoolFiles()
    {
        try
        {
            return Directory.GetFiles(Path.GetTempPath(), "mf_stream_*").Length;
        }
        catch
        {
            return 0;
        }
    }

    public static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (predicate())
                return true;
            Thread.Sleep(25);
        }

        return predicate();
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* ignored */ }
    }
}

using System.Runtime.InteropServices;
using NDILib;

namespace S.Media.NDI.Input;

internal static class NdiAudioFrameConverter
{
    public static int CopyInterleaved32f(in NDIAudioFrameV3 audio, Span<float> dst)
    {
        var channels = audio.NoChannels;
        var samples = audio.NoSamples;
        var totalFloats = checked(samples * channels);
        if (totalFloats <= 0 || dst.Length < totalFloats)
            return 0;

        var interleaved = new NDIAudioInterleaved32f
        {
            SampleRate = audio.SampleRate,
            NoChannels = channels,
            NoSamples = samples,
        };

        var scratch = new float[totalFloats];
        var pin = GCHandle.Alloc(scratch, GCHandleType.Pinned);
        try
        {
            interleaved.PData = pin.AddrOfPinnedObject();
            if (!NDIAudioUtils.ToInterleaved32f(audio, ref interleaved))
                return 0;
            scratch.AsSpan(0, totalFloats).CopyTo(dst);
            return totalFloats;
        }
        finally
        {
            pin.Free();
        }
    }
}

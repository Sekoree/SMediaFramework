using MALib;

namespace S.Media.MiniAudio;

public sealed class MiniAudioException : Exception
{
    public MiniAudioException(string message) : base(message)
    {
    }

    internal static void ThrowIfError(int result, string operation)
    {
        if (result == MiniAudioNative.Success)
            return;

        throw new MiniAudioException($"{operation} failed: {MiniAudioNative.ResultDescription(result)} ({result})");
    }
}

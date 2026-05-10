namespace S.Media.PortAudio;

public sealed class PortAudioException : Exception
{
    public int ErrorCode { get; }

    internal PortAudioException(PaError code, string operation)
        : base($"{operation} failed: {Native.Pa_GetErrorText(code) ?? "unknown"} ({(int)code})")
    {
        ErrorCode = (int)code;
    }

    internal static void ThrowIfError(PaError code, string operation)
    {
        if (code != PaError.paNoError)
            throw new PortAudioException(code, operation);
    }
}

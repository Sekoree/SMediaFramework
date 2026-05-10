namespace S.Media.Core.Audio;

/// <summary>
/// Canonical audio stream description shared by every source and sink:
/// FFmpeg-decoded files, NDI receivers, PortAudio devices, etc.
/// </summary>
/// <remarks>
/// All audio crossing the framework's pipelines is packed (interleaved)
/// 32-bit float; sources convert at their boundary. This avoids carrying a
/// sample-format tag through the mixer and matches what most sinks want
/// natively.
/// </remarks>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public int BytesPerSample => sizeof(float);
    public int BytesPerFrame => Channels * sizeof(float);
}

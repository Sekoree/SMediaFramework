namespace S.Media.Core.Audio;

/// <summary>
/// Canonical audio stream description shared by every source and output:
/// FFmpeg-decoded files, NDI receivers, PortAudio devices, etc.
/// </summary>
/// <remarks>
/// <para>
/// All audio crossing the framework's pipelines is packed (interleaved)
/// 32-bit float; sources convert at their boundary. This avoids carrying a
/// sample-format tag through the mixer and matches what most outputs want
/// natively.
/// </para>
/// <para>
/// The struct is a plain value-type record so <c>default</c> / <c>AudioFormat(0, 0)</c>
/// remains valid as a "no audio" sentinel (used by
/// <see cref="S.Media.FFmpeg.MediaContainerDecoder.Audio"/> when a container has no audio
/// stream — consumers must guard with <c>HasAudio</c> before reading the format). The
/// constructor therefore does <strong>not</strong> validate; entry points that plumb a
/// format into a live pipeline (router, player, resampler) call <see cref="Validate"/>
/// so invalid values fail fast at the API boundary.
/// </para>
/// </remarks>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public int BytesPerSample => sizeof(float);
    public int BytesPerFrame => Channels * sizeof(float);

    /// <summary>True when both <see cref="SampleRate"/> and <see cref="Channels"/> are positive.</summary>
    public bool IsValid => SampleRate > 0 && Channels > 0;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <see cref="SampleRate"/> or <see cref="Channels"/>
    /// is non-positive. Call at every public API entry point that wires a format into a live pipeline
    /// (AudioPlayer, AudioRouter, outputs) so invalid values fail at the boundary rather than as a later
    /// zero-sized allocation or silent passthrough.
    /// </summary>
    /// <param name="paramName">Optional parameter name surfaced in the exception (for ArgumentException-style call sites).</param>
    public void Validate(string? paramName = null)
    {
        if (SampleRate <= 0)
            throw new ArgumentException(
                $"AudioFormat.SampleRate must be positive (was {SampleRate}, channels={Channels}).",
                paramName);
        if (Channels <= 0)
            throw new ArgumentException(
                $"AudioFormat.Channels must be positive (was {Channels}, sampleRate={SampleRate}).",
                paramName);
    }
}

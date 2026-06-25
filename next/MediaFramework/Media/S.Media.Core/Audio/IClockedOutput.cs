namespace S.Media.Core.Audio;

/// <summary>
/// Optional <see cref="IAudioOutput"/> capability: the output can pace
/// <see cref="AudioRouter"/> production against its actual consumption rate.
/// </summary>
/// <remarks>
/// <para>
/// Implement this when the output has an authoritative clock — typically a
/// hardware audio device (PortAudio output reports samples-played via its
/// audio thread). Network senders that block their own <see cref="IAudioOutput.Submit"/>
/// for clocking (e.g. NDI with <c>clockAudio = true</c>) generally don't need
/// to implement this; the OutputPump's drainer thread absorbs their pacing.
/// </para>
/// <para>
/// Pair with <see cref="OutputSlavedRouterClock"/>: the router calls
/// <see cref="WaitForCapacity"/> once per chunk and only produces when the
/// output is ready, giving sample-accurate sync between producer and consumer.
/// </para>
/// </remarks>
public interface IClockedOutput
{
    /// <summary>
    /// Block until <paramref name="chunkSamples"/> samples per channel can be
    /// enqueued without overflow. Returns true when ready, false on cancel.
    /// </summary>
    bool WaitForCapacity(int chunkSamples, CancellationToken token);
}

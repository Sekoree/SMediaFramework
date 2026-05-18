namespace S.Media.Core.Audio;

/// <summary>Argument for <see cref="AudioRouter.SinkErrored"/>.</summary>
public sealed class AudioRouterSinkErrorEventArgs : EventArgs
{
    public string SinkId { get; }
    public Exception Exception { get; }

    public AudioRouterSinkErrorEventArgs(string sinkId, Exception exception)
    {
        SinkId = sinkId;
        Exception = exception;
    }
}

/// <summary>
/// Argument for <see cref="AudioRouter.PumpPressure"/> when chunks are dropped.
/// <c>readonly record struct</c> so the steady-state path under sustained drop pressure stays
/// zero-alloc; handlers receive a value copy.
/// </summary>
/// <param name="SinkId">Router sink id whose pump dropped a chunk.</param>
/// <param name="DroppedTotal">Total dropped chunks on this pump after the latest drop.</param>
public readonly record struct AudioRouterPumpPressureEventArgs(string SinkId, long DroppedTotal);

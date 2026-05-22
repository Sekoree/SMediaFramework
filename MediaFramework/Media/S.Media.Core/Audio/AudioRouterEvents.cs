namespace S.Media.Core.Audio;

/// <summary>Argument for <see cref="AudioRouter.OutputErrored"/>.</summary>
public sealed class AudioRouterOutputErrorEventArgs : EventArgs
{
    public string SinkId { get; }
    public Exception Exception { get; }

    public AudioRouterOutputErrorEventArgs(string outputId, Exception exception)
    {
        SinkId = outputId;
        Exception = exception;
    }
}

/// <summary>
/// Argument for <see cref="AudioRouter.PumpPressure"/> when chunks are dropped.
/// <c>readonly record struct</c> so the steady-state path under sustained drop pressure stays
/// zero-alloc; handlers receive a value copy.
/// </summary>
/// <param name="SinkId">Router output id whose pump dropped a chunk.</param>
/// <param name="DroppedTotal">Total dropped chunks on this pump after the latest drop.</param>
public readonly record struct AudioRouterPumpPressureEventArgs(string SinkId, long DroppedTotal);

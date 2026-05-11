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

/// <summary>Argument for <see cref="AudioRouter.PumpPressure"/> when chunks are dropped.</summary>
public sealed class AudioRouterPumpPressureEventArgs : EventArgs
{
    public string SinkId { get; }
    /// <summary>Total dropped chunks on this pump after the latest drop.</summary>
    public long DroppedTotal { get; }

    public AudioRouterPumpPressureEventArgs(string sinkId, long droppedTotal)
    {
        SinkId = sinkId;
        DroppedTotal = droppedTotal;
    }
}

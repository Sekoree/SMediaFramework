namespace S.Media.FFmpeg.Video;

/// <summary>Raised by <see cref="VideoSinkPump"/> when the bounded queue drops an oldest frame (same role as <see cref="S.Media.Core.Audio.AudioRouter.PumpPressure"/> for audio).</summary>
public sealed class VideoSinkPumpPressureEventArgs : EventArgs
{
    /// <summary>Drainer thread / pump label (often the router output id).</summary>
    public string PumpName { get; }

    /// <summary>Running total of frames dropped due to a full queue since this pump was configured.</summary>
    public long DroppedFramesTotal { get; }

    public VideoSinkPumpPressureEventArgs(string pumpName, long droppedFramesTotal)
    {
        PumpName = pumpName;
        DroppedFramesTotal = droppedFramesTotal;
    }
}

/// <summary>Raised by <see cref="VideoRouter"/> when an async <see cref="VideoSinkPump"/> attached to an output drops a frame.</summary>
public sealed class VideoRouterPumpPressureEventArgs : EventArgs
{
    public string OutputId { get; }

    /// <summary>Running total of frames dropped on that output's pump since configure.</summary>
    public long DroppedFramesTotal { get; }

    public VideoRouterPumpPressureEventArgs(string outputId, long droppedFramesTotal)
    {
        OutputId = outputId;
        DroppedFramesTotal = droppedFramesTotal;
    }
}

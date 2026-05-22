namespace S.Media.Core.Video;

/// <summary>
/// Raised by <see cref="VideoOutputPump"/> when the bounded queue drops an oldest frame (video analogue of
/// <see cref="S.Media.Core.Audio.AudioRouter.PumpPressure"/>). <c>readonly record struct</c> so sustained
/// drop pressure does not allocate.
/// </summary>
/// <param name="PumpName">Drainer thread / pump label (often the router output id).</param>
/// <param name="DroppedFramesTotal">Running total of frames dropped due to a full queue since this pump was configured.</param>
public readonly record struct VideoOutputPumpPressureEventArgs(string PumpName, long DroppedFramesTotal);

/// <summary>
/// Raised by <see cref="VideoRouter"/> when an async <see cref="VideoOutputPump"/> attached to an output drops a frame.
/// <c>readonly record struct</c> so sustained drop pressure does not allocate.
/// </summary>
/// <param name="OutputId">Router output id whose pump dropped a frame.</param>
/// <param name="DroppedFramesTotal">Running total of frames dropped on that output's pump since configure.</param>
public readonly record struct VideoRouterPumpPressureEventArgs(string OutputId, long DroppedFramesTotal);

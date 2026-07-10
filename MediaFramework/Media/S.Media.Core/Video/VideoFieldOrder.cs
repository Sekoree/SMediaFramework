namespace S.Media.Core.Video;

/// <summary>
/// Field order on an interlaced frame (<see cref="VideoFrame.FieldOrder"/>). Producers populate this
/// from container/codec metadata; deinterlacers and downstream senders (NDI) read it.
/// </summary>
public enum VideoFieldOrder : byte
{
    /// <summary>Progressive frame - no fields. Default for the vast majority of content.</summary>
    Progressive = 0,
    /// <summary>Interlaced, top field is temporally first.</summary>
    TopFieldFirst = 1,
    /// <summary>Interlaced, bottom field is temporally first.</summary>
    BottomFieldFirst = 2,
    /// <summary>Interlaced but parity is unknown / not reported.</summary>
    Interlaced = 3,
}

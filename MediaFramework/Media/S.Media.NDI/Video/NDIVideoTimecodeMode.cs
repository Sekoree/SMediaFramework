namespace S.Media.NDI.Video;

/// <summary>
/// How <see cref="NDIVideoSender"/> fills <see cref="NDILib.NDIVideoFrameV2.Timecode"/> (and related fields)
/// when packing frames for <see cref="NDILib.NDISender.SendVideoAsync"/>.
/// </summary>
public enum NDIVideoTimecodeMode
{
    /// <summary>
    /// Pass <see cref="NDILib.NDIConstants.TimecodeSynthesize"/> so the NDI runtime assigns timecode
    /// (legacy behaviour).
    /// </summary>
    Synthesize,

    /// <summary>
    /// Use <c>(<see cref="S.Media.Core.Video.VideoFrame.PresentationTime"/> − session anchor).Ticks</c>
    /// as the frame timecode (100 ns units - same as <see cref="System.TimeSpan.Ticks"/>), so the video
    /// stream carries an explicit timeline comparable to <see cref="NDIAudioOutput"/>'s sample-based
    /// 100 ns timecodes when both streams share the same muxed source and start together.
    /// </summary>
    /// <remarks>
    /// The anchor is the first submitted presentation time (audio or video) after
    /// <see cref="S.Media.Core.Video.IVideoOutput.Configure"/> on the video output, or after
    /// <see cref="NDIVideoSender.ResetPresentationTimecodeAnchor"/> / <see cref="NDIOutput.ResetVideoPresentationTimecodeAnchor"/>.
    /// A backward jump of more than one second re-anchors so seeks do not produce negative timecodes.
    /// <see cref="NDILib.NDIVideoFrameV2.Timestamp"/> is set to <see cref="NDILib.NDIConstants.TimestampUndefined"/>.
    /// </remarks>
    PresentationRelativeTicks,

    /// <summary>
    /// Use <see cref="S.Media.Core.Video.VideoFrame.PresentationTime"/>.<see cref="System.TimeSpan.Ticks"/> directly
    /// (100 ns units) so video NDI timecodes share the same mux timeline as audio when
    /// <see cref="S.Media.NDI.Audio.NDIAudioOutput"/> stamps from <see cref="S.Media.Core.Audio.AudioFrame.PresentationTime"/>.
    /// </summary>
    MuxerPresentationTicks,

    /// <summary>
    /// Read SMPTE 12M timecode from <see cref="S.Media.Core.Video.VideoFrame.Timecode"/> and encode it as
    /// 100-ns ticks (matching NDI's timecode slot semantics) - so e.g. <c>01:23:45:00</c> at 30 fps lands
    /// as <c>(1·3600 + 23·60 + 45)·10⁷</c> ticks. Frames without a timecode fall back to the same logic
    /// as <see cref="PresentationRelativeTicks"/>.
    /// </summary>
    SmpteFromFrame,
}

namespace S.Media.Core.Diagnostics;

/// <summary>Optional overflow counters for live NDI ingest (implemented by <c>NDISource</c>).</summary>
public interface INDIOverflowReporter
{
    long AudioOverflowFloats { get; }
    long VideoOverflowFrames { get; }
}

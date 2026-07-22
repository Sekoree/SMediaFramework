using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// Owns one already-rendered static frame for a borrowed local output. The output retains its uploaded texture,
/// so the frame is submitted once when the slate is acquired rather than being pumped as pretend video.
/// </summary>
internal sealed class StaticSlateVideoOutput : IDisposable
{
    private readonly IVideoOutput _inner;
    private readonly object _gate = new();
    private VideoFormat _format;
    private VideoFrame? _template;
    private bool _configured;
    private bool _disposed;

    public StaticSlateVideoOutput(IVideoOutput inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Configure(format);
        _format = format;
        _configured = true;
    }

    /// <summary>Takes ownership of <paramref name="template"/>. It must already match the output format.</summary>
    public void SetTemplate(VideoFrame template)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(template);
        if (!_configured)
        {
            template.Dispose();
            throw new InvalidOperationException("Configure the slate output before setting its template.");
        }
        if (template.Format != _format)
        {
            template.Dispose();
            throw new ArgumentException(
                $"Slate template format {template.Format} does not match configured output {_format}.",
                nameof(template));
        }

        VideoFrame? previous;
        lock (_gate)
        {
            previous = _template;
            _template = template;
        }
        previous?.Dispose();
    }

    public void Submit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrame? template;
        lock (_gate)
            template = _template;
        if (template is null)
            return;

        // The borrowed output may consume asynchronously (Avalonia keeps a pending frame until the next GL
        // render), so hand it an independently-owned pooled copy rather than aliasing the template backing.
        var frame = VideoFrameCpuClone.DuplicateCpuBacking(template, template.ColorTransferHint);
        try
        {
            _inner.Submit(frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        VideoFrame? template;
        lock (_gate)
        {
            template = _template;
            _template = null;
        }
        template?.Dispose();
        // The output is borrowed from OutputManagementViewModel and released separately by the session.
    }
}

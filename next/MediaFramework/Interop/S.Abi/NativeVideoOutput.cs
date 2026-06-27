using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpVideoOutputVTable</c> to the managed <see cref="IVideoOutput"/> — plus optional
/// <see cref="IVideoOutputQueueControl"/> when the plugin provides abandon/wait-idle. <see cref="Submit"/> pins the
/// managed frame's CPU planes and views them as an <c>MfpVideoFrame</c> for the synchronous native submit, then
/// unpins. Only CPU frames are marshalled (GPU-handle output is the zero-copy path, not handled here).
/// </summary>
public sealed unsafe class NativeVideoOutput : IVideoOutput, IVideoOutputQueueControl, IDisposable
{
    private readonly MfpVideoOutputVTable* _vt;
    private readonly void* _self;
    private readonly PixelFormat[] _accepted;
    private VideoFormat _format;
    private bool _disposed;

    private NativeVideoOutput(MfpVideoOutputVTable* vt, void* self, PixelFormat[] accepted)
    {
        _vt = vt;
        _self = self;
        _accepted = accepted;
    }

    public static NativeVideoOutput Create(nint vtable, nint self)
    {
        var vt = (MfpVideoOutputVTable*)vtable;
        var s = (void*)self;
        return new NativeVideoOutput(vt, s, QueryAccepted(vt, s));
    }

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

    public void Configure(VideoFormat format)
    {
        _format = format;
        if (_vt->Configure == null)
            return;

        var mf = new MfpVideoFormat
        {
            Width = (uint)format.Width,
            Height = (uint)format.Height,
            PixelFormat = AbiFrameMarshal.FromCore(format.PixelFormat),
            FpsNum = (uint)Math.Max(0, format.FrameRate.Numerator),
            FpsDen = (uint)Math.Max(0, format.FrameRate.Denominator),
        };
        if (_vt->Configure(_self, &mf) != (int)MfpStatus.Ok)
            throw new InvalidOperationException($"plugin video output rejected configure for {format.PixelFormat}.");
    }

    public void Submit(VideoFrame frame)
    {
        if (_disposed || _vt->Submit == null || frame is null)
            return;

        var handles = AbiFrameMarshal.ToNativeCpuFrame(frame, out var mf);
        try
        {
            _vt->Submit(_self, &mf);
        }
        finally
        {
            foreach (var h in handles)
                h.Dispose();
        }
    }

    public void AbandonQueuedFrames()
    {
        if (_vt->AbandonQueued != null)
            _vt->AbandonQueued(_self);
    }

    public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_vt->WaitForIdle == null)
            return true; // no internal queue => already idle
        var ms = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        return _vt->WaitForIdle(_self, ms) != 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_self);
    }

    private static PixelFormat[] QueryAccepted(MfpVideoOutputVTable* vt, void* s)
    {
        if (vt->AcceptedPixelFormats == null)
            return [];
        const int cap = 32;
        var buf = stackalloc int[cap];
        var count = 0;
        if (vt->AcceptedPixelFormats(s, buf, cap, &count) != (int)MfpStatus.Ok || count <= 0)
            return [];
        count = Math.Min(count, cap);
        var result = new PixelFormat[count];
        for (var i = 0; i < count; i++)
            result[i] = AbiFrameMarshal.ToCore(buf[i]);
        return result;
    }
}

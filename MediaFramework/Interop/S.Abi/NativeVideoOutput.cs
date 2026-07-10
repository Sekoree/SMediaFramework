using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpVideoOutputVTable</c> to the managed <see cref="IVideoOutput"/> - plus optional
/// <see cref="IVideoOutputQueueControl"/> when the plugin provides abandon/wait-idle. <see cref="Submit"/> pins the
/// managed frame's CPU planes or hardware backing as an <c>MfpVideoFrame</c> for the synchronous native submit.
/// </summary>
public sealed unsafe class NativeVideoOutput : IVideoOutput, IVideoOutputQueueControl, IDisposable
{
    private readonly MfpVideoOutputVTable* _vt;
    private readonly void* _self;
    private readonly PixelFormat[] _accepted;
    private readonly AbiPluginLease _lease;
    private VideoFormat _format;
    private bool _disposed;

    private NativeVideoOutput(
        MfpVideoOutputVTable* vt, void* self, PixelFormat[] accepted, AbiPluginLease lease)
    {
        _vt = vt;
        _self = self;
        _accepted = accepted;
        _lease = lease;
    }

    internal static NativeVideoOutput Create(nint vtable, nint self, AbiPluginLease lease)
    {
        var vt = (MfpVideoOutputVTable*)vtable;
        var s = (void*)self;
        return new NativeVideoOutput(vt, s, QueryAccepted(vt, s), lease);
    }

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        AbiPluginHost.ClearLastError();
        var status = _vt->Configure(_self, &mf);
        if (status != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException(
                $"plugin video output configure for {format.PixelFormat}", status);
    }

    public void Submit(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (_vt->Submit == null)
            return;

        var kind = frame.HardwareBacking switch
        {
            DmabufNv12Backing or DmabufP010Backing or DmabufP016Backing => MfpFrameKind.DmaBuf,
            Win32SharedNv12Backing => MfpFrameKind.D3D11,
            _ => MfpFrameKind.Cpu,
        };
        if ((_vt->AcceptedFrameKinds & (1u << (int)kind)) == 0)
            throw new NotSupportedException($"plugin video output does not accept ABI frame kind {kind}.");
        if ((_vt->AcceptedSyncKinds & 1u) == 0)
            throw new NotSupportedException("plugin video output does not accept MFP_SYNC_NONE frames.");

        var handles = AbiFrameMarshal.ToNativeFrame(frame, out var mf);
        try
        {
            AbiPluginHost.ClearLastError();
            var status = _vt->Submit(_self, &mf);
            if (status != (int)MfpStatus.Ok)
                throw AbiPluginHost.StatusException("plugin video output submit", status);
        }
        finally
        {
            foreach (var h in handles)
                h.Dispose();
        }
    }

    public void AbandonQueuedFrames()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->AbandonQueued != null)
            _vt->AbandonQueued(_self);
    }

    public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_vt->WaitForIdle == null)
            return true; // no internal queue => already idle
        ObjectDisposedException.ThrowIf(_disposed, this);
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            var ms = (int)Math.Clamp(remaining.TotalMilliseconds, 0, 50);
            if (_vt->WaitForIdle(_self, ms) != 0)
                return true;
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lease.Dispose();
    }

    private static PixelFormat[] QueryAccepted(MfpVideoOutputVTable* vt, void* s)
    {
        if (vt->AcceptedPixelFormats == null)
            return [];
        const int cap = 32;
        var buf = stackalloc int[cap];
        var count = 0;
        AbiPluginHost.ClearLastError();
        var status = vt->AcceptedPixelFormats(s, buf, cap, &count);
        if (status != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("plugin video output pixel-format query", status);
        if (count <= 0)
            return [];
        count = Math.Min(count, cap);
        var result = new PixelFormat[count];
        for (var i = 0; i < count; i++)
            result[i] = AbiFrameMarshal.ToCore(buf[i]);
        return result;
    }
}

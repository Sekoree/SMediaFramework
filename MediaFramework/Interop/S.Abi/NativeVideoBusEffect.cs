using System.Text;
using S.Media.Core.Buses;
using S.Media.Core.Video;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpVideoEffectFactoryVTable</c> to the managed bus registry - the video
/// twin of <see cref="NativeAudioEffectFactory"/>. V1 contract: CPU frames are pinned and mutated IN
/// PLACE through the vtable (the pass-through mode of <see cref="IVideoBusEffect"/>); hardware-backed
/// frames bypass the plugin unchanged. The hosts' M5 retire-on-processing-thread contract makes native
/// hot-swap safe (never destroyed under a running Process).
/// </summary>
public sealed unsafe class NativeVideoEffectFactory : IDisposable
{
    private readonly MfpVideoEffectFactoryVTable* _vt;
    private readonly void* _self;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeVideoEffectFactory(nint vtable, nint self, AbiPluginLease lease)
    {
        _vt = (MfpVideoEffectFactoryVTable*)vtable;
        _self = (void*)self;
        _lease = lease;
    }

    public IVideoBusEffect Create(string? configJson)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[]? configUtf8 = null;
        if (configJson is not null)
        {
            var len = Encoding.UTF8.GetByteCount(configJson);
            configUtf8 = new byte[len + 1];
            Encoding.UTF8.GetBytes(configJson, configUtf8);
        }

        AbiPluginHost.ClearLastError();
        void* instance;
        fixed (byte* cfg = configUtf8)
            instance = _vt->Create(_self, cfg);
        if (instance == null)
            throw AbiPluginHost.StatusException("video-effect create", (int)MfpStatus.ErrInternal);

        return new NativeVideoBusEffect(_vt->EffectVTable, instance, _lease.AcquireDependent());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_self);
        _lease.Dispose();
    }
}

/// <summary>One created native video-effect instance behind <see cref="IVideoBusEffect"/>.</summary>
internal sealed unsafe class NativeVideoBusEffect : IVideoBusEffect
{
    private readonly MfpVideoEffectVTable* _vt;
    private readonly void* _effect;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeVideoBusEffect(MfpVideoEffectVTable* vt, void* effect, AbiPluginLease lease)
    {
        _vt = vt;
        _effect = effect;
        _lease = lease;
    }

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->Configure == null)
            return;
        var native = new MfpVideoFormat
        {
            Width = (uint)format.Width,
            Height = (uint)format.Height,
            PixelFormat = AbiFrameMarshal.FromCore(format.PixelFormat),
            FpsNum = (uint)Math.Max(0, format.FrameRate.Numerator),
            FpsDen = (uint)Math.Max(0, format.FrameRate.Denominator),
        };
        AbiPluginHost.ClearLastError();
        var rc = _vt->Configure(_effect, &native);
        if (rc != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("video-effect configure", rc);
    }

    public VideoFrame Process(VideoFrame frame, TimeSpan presentationTime)
    {
        if (_disposed || _vt->Process == null)
            return frame;
        // V1: CPU frames only - a hardware-backed frame passes through untouched (the header contract).
        if (frame.HardwareBacking is not null)
            return frame;

        var handles = AbiFrameMarshal.ToNativeFrame(frame, out var mf);
        try
        {
            _vt->Process(_effect, &mf, presentationTime.Ticks); // in-place; status ignored on the pump path
        }
        finally
        {
            foreach (var h in handles)
                h.Dispose();
        }

        return frame; // mutated in place - same ownership as the managed pass-through mode
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_effect);
        _lease.Dispose();
    }
}

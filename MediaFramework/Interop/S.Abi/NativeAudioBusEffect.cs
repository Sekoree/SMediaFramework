using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Buses;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpAudioEffectFactoryVTable</c> to the managed bus registry: each factory
/// registers under its `kind` and builds <see cref="IAudioBusEffect"/> instances from the host's opaque
/// per-insert JSON config - indistinguishable from a built-in effect once inserted.
///
/// <para><strong>Real-time boundary:</strong> <c>Process</c> forwards straight through the function
/// pointer with the chunk pinned - no allocation, no marshaling copies. The RT rules the header imposes
/// on the plugin (bounded work, no host reentry) are the same ones <see cref="IAudioBusEffect"/> imposes
/// on managed effects, so nothing extra is needed at this seam. The M5 retire-on-processing-thread
/// contract in the hosts is what makes hot-swapping a NATIVE effect safe: it is never destroyed while a
/// Process could still be executing.</para>
/// </summary>
public sealed unsafe class NativeAudioEffectFactory : IDisposable
{
    private readonly MfpAudioEffectFactoryVTable* _vt;
    private readonly void* _self;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeAudioEffectFactory(nint vtable, nint self, AbiPluginLease lease)
    {
        _vt = (MfpAudioEffectFactoryVTable*)vtable;
        _self = (void*)self;
        _lease = lease;
    }

    /// <summary>Creates one effect instance (throws when the plugin returns NULL - the registry's
    /// factory contract; the host surfaces the plugin's last-error detail).</summary>
    public IAudioBusEffect Create(string? configJson)
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
            throw AbiPluginHost.StatusException("audio-effect create", (int)MfpStatus.ErrInternal);

        return new NativeAudioBusEffect(_vt->EffectVTable, instance, _lease.AcquireDependent());
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

/// <summary>One created native effect instance behind <see cref="IAudioBusEffect"/>.</summary>
internal sealed unsafe class NativeAudioBusEffect : IAudioBusEffect
{
    private readonly MfpAudioEffectVTable* _vt;
    private readonly void* _effect;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeAudioBusEffect(MfpAudioEffectVTable* vt, void* effect, AbiPluginLease lease)
    {
        _vt = vt;
        _effect = effect;
        _lease = lease;
    }

    public void Configure(AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->Configure == null)
            return;
        var native = new MfpAudioFormat
        {
            SampleRate = (uint)format.SampleRate,
            Channels = (uint)format.Channels,
            SampleFormat = 0, // f32 interleaved - the only bus format
        };
        AbiPluginHost.ClearLastError();
        var rc = _vt->Configure(_effect, &native);
        if (rc != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("audio-effect configure", rc);
    }

    public void Process(Span<float> interleaved, long samplePosition)
    {
        // RT path: no throw, no allocation - a faulted plugin call surfaces as unprocessed audio, and
        // the plugin's set_last_error is visible in the host log at the next non-RT touchpoint.
        if (_disposed || _vt->Process == null || interleaved.IsEmpty)
            return;
        fixed (float* samples = interleaved)
            _vt->Process(_effect, samples, interleaved.Length, samplePosition);
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

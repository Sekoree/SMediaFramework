using System.Runtime.InteropServices;
using System.Diagnostics;
using S.Control;
using S.Media.Compositor;
using S.Media.Core.Registry;

namespace S.Abi;

/// <summary>A capability registered by a loaded plugin. The raw pointers remain valid until the plugin unloads.</summary>
public sealed record AbiRegisteredCapability(string Capability, string Id, nint VTable, nint Self)
{
    internal IReadOnlyList<nint> OwnedVTables { get; init; } = [VTable];
}

/// <summary>
/// A loaded native plugin. Calling <see cref="Dispose"/> requests unload; the library remains loaded until every
/// adapter and native instance created from it has also been disposed.
/// </summary>
public sealed unsafe class AbiLoadedPlugin : IDisposable
{
    private readonly object _gate = new();
    private nint _lib;
    private readonly delegate* unmanaged<void> _unregister;
    private int _leaseCount;
    private bool _unloadRequested;
    private bool _unloading;

    internal AbiLoadedPlugin(
        nint lib,
        delegate* unmanaged<void> unregister,
        string id,
        string displayName,
        uint capabilities,
        IReadOnlyList<AbiRegisteredCapability> registered)
    {
        _lib = lib;
        _unregister = unregister;
        Id = id;
        DisplayName = displayName;
        Capabilities = capabilities;
        Registered = registered;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public uint Capabilities { get; }
    public IReadOnlyList<AbiRegisteredCapability> Registered { get; }

    public bool IsUnloadRequested
    {
        get { lock (_gate) return _unloadRequested; }
    }

    public bool IsUnloaded
    {
        get { lock (_gate) return _lib == 0; }
    }

    internal AbiPluginLease AcquireLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_lib == 0 || _unloading, this);
            _leaseCount++;
            return new AbiPluginLease(this);
        }
    }

    internal void ReleaseLease()
    {
        lock (_gate)
        {
            if (_leaseCount <= 0)
                return;
            _leaseCount--;
        }
        TryUnload();
    }

    public void Dispose()
    {
        lock (_gate)
            _unloadRequested = true;
        TryUnload();
        GC.SuppressFinalize(this);
    }

    ~AbiLoadedPlugin() => Dispose();

    private void TryUnload()
    {
        nint lib;
        lock (_gate)
        {
            if (!_unloadRequested || _leaseCount != 0 || _lib == 0 || _unloading)
                return;
            _unloading = true;
            lib = _lib;
        }

        try
        {
            AbiPluginHost.DestroyRegisteredCapabilities(Registered);
            if (_unregister != null)
                _unregister();
        }
        finally
        {
            AbiPluginHost.FreeRegisteredVTables(Registered);
            NativeLibrary.Free(lib);
            lock (_gate)
            {
                _lib = 0;
                _unloading = false;
            }
        }
    }
}

internal sealed class AbiPluginLease : IDisposable
{
    private AbiLoadedPlugin? _plugin;

    internal AbiPluginLease(AbiLoadedPlugin plugin) => _plugin = plugin;

    internal AbiPluginLease AcquireDependent() =>
        (_plugin ?? throw new ObjectDisposedException(nameof(AbiPluginLease))).AcquireLease();

    public void Dispose()
    {
        Interlocked.Exchange(ref _plugin, null)?.ReleaseLease();
        GC.SuppressFinalize(this);
    }

    ~AbiPluginLease() => Dispose();
}

/// <summary>Loads native plugins and adapts their registered capabilities to scoped framework registries.</summary>
public static unsafe class AbiPluginHost
{
    public const uint AbiVersion = 1u << 16;

    [ThreadStatic] private static string? t_lastErrorMessage;

    public static string? LastLogMessage { get; private set; }
    public static string? LastErrorMessage => t_lastErrorMessage;

    private static readonly nint s_hostApiPtr = CreateHostApi();

    private static nint CreateHostApi()
    {
        var p = (MfpHostApi*)NativeMemory.Alloc((nuint)sizeof(MfpHostApi));
        *p = new MfpHostApi
        {
            AbiVersion = AbiVersion,
            StructSize = (uint)sizeof(MfpHostApi),
            Log = &HostLog,
            SetLastError = &HostSetLastError,
            NowTicks = &HostNowTicks,
            SupportedFrameKinds = SupportedFrameKinds(),
            SupportedSyncKinds = 1u, // MFP_SYNC_NONE
        };
        return (nint)p;
    }

    public static AbiLoadedPlugin Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var lib = NativeLibrary.Load(path);
        var registration = new RegistrationState();
        delegate* unmanaged<void> unregister = null;
        try
        {
            var register = (delegate* unmanaged<MfpHostApi*, MfpPluginInfo*, MfpRegistrar*, int>)
                NativeLibrary.GetExport(lib, "mfp_plugin_register");
            if (NativeLibrary.TryGetExport(lib, "mfp_plugin_unregister", out var unregisterPtr))
                unregister = (delegate* unmanaged<void>)unregisterPtr;

            var ctx = GCHandle.Alloc(registration);
            try
            {
                var reg = new MfpRegistrar
                {
                    AbiVersion = AbiVersion,
                    StructSize = (uint)sizeof(MfpRegistrar),
                    Ctx = (void*)GCHandle.ToIntPtr(ctx),
                    AddAudioBackend = &AddAudioBackend,
                    AddMediaSourceProvider = &AddMediaSourceProvider,
                    AddVideoOutput = &AddVideoOutput,
                    AddLayerSurface = &AddLayerSurface,
                    AddSubtitleProvider = &AddSubtitleProvider,
                    AddControlDecoder = &AddControlDecoder,
                };

                var info = new MfpPluginInfo
                {
                    AbiVersion = AbiVersion,
                    StructSize = (uint)sizeof(MfpPluginInfo),
                };
                ClearLastError();
                var rc = register((MfpHostApi*)s_hostApiPtr, &info, &reg);
                if (rc != (int)MfpStatus.Ok)
                    throw StatusException($"mfp_plugin_register('{path}')", rc);
                if (registration.Failure is { } registrationFailure)
                    throw new InvalidOperationException(
                        $"plugin '{path}' ignored a failed capability registration: {registrationFailure}");
                ValidateVersionAndSize(info.AbiVersion, info.StructSize,
                    RequiredSize<MfpPluginInfo>(nameof(MfpPluginInfo.Capabilities)), $"plugin info from '{path}'");
                var pluginId = Utf8(info.Id);
                if (string.IsNullOrWhiteSpace(pluginId))
                    throw new InvalidOperationException($"plugin '{path}' returned an empty id.");

                var plugin = new AbiLoadedPlugin(
                    lib, unregister, pluginId, Utf8(info.DisplayName), info.Capabilities,
                    registration.Capabilities.ToArray());
                lib = 0;
                registration.Capabilities.Clear();
                return plugin;
            }
            finally
            {
                ctx.Free();
            }
        }
        finally
        {
            if (lib != 0)
            {
                DestroyRegisteredCapabilities(registration.Capabilities);
                if (unregister != null)
                    unregister();
                FreeRegisteredVTables(registration.Capabilities);
                NativeLibrary.Free(lib);
            }
        }
    }

    public static IReadOnlyList<(string Id, IControlMeterBlobDecoder Decoder)> BindControlDecoders(AbiLoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var result = new List<(string, IControlMeterBlobDecoder)>();
        foreach (var cap in plugin.Registered)
            if (cap.Capability == "control-decoder")
                result.Add((cap.Id, new NativeControlDecoder(cap.VTable, cap.Self, plugin.AcquireLease())));
        return result;
    }

    public static IReadOnlyList<(string Scheme, NativeMediaSourceProvider Provider)> BindMediaSourceProviders(AbiLoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var result = new List<(string, NativeMediaSourceProvider)>();
        foreach (var cap in plugin.Registered)
            if (cap.Capability == "media-source-provider")
                result.Add((cap.Id, new NativeMediaSourceProvider(cap.Id, cap.VTable, cap.Self, plugin.AcquireLease())));
        return result;
    }

    public static IReadOnlyList<(string Id, NativeAudioBackend Backend)> BindAudioBackends(AbiLoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var result = new List<(string, NativeAudioBackend)>();
        foreach (var cap in plugin.Registered)
            if (cap.Capability == "audio-backend")
                result.Add((cap.Id, new NativeAudioBackend(cap.Id, cap.VTable, cap.Self, plugin.AcquireLease())));
        return result;
    }

    public static IReadOnlyList<(string Id, NativeVideoOutput Output)> BindVideoOutputs(AbiLoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var result = new List<(string, NativeVideoOutput)>();
        foreach (var cap in plugin.Registered)
            if (cap.Capability == "video-output")
                result.Add((cap.Id, NativeVideoOutput.Create(cap.VTable, cap.Self, plugin.AcquireLease())));
        return result;
    }

    public static IReadOnlyList<(string Id, NativeSubtitleProvider Provider)> BindSubtitleProviders(AbiLoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var result = new List<(string, NativeSubtitleProvider)>();
        foreach (var cap in plugin.Registered)
            if (cap.Capability == "subtitle-provider")
                result.Add((cap.Id, new NativeSubtitleProvider(cap.VTable, cap.Self, plugin.AcquireLease())));
        return result;
    }

    public static IReadOnlyList<(string Kind, NativeLayerSurfaceFactory Factory)> BindLayerSurfaces(AbiLoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var result = new List<(string, NativeLayerSurfaceFactory)>();
        foreach (var cap in plugin.Registered)
            if (cap.Capability == "layer-surface")
                result.Add((cap.Id, new NativeLayerSurfaceFactory(cap.VTable, cap.Self, plugin.AcquireLease())));
        return result;
    }

    public static void RegisterInto(
        AbiLoadedPlugin plugin,
        IMediaRegistryBuilder? media = null,
        ControlMeterBlobDecoderRegistry? control = null,
        ICompositorRegistryBuilder? compositor = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        if (media is not null)
        {
            foreach (var (_, provider) in BindMediaSourceProviders(plugin))
                media.AddDecoder(provider);
            foreach (var (_, backend) in BindAudioBackends(plugin))
                media.AddAudioBackend(backend);
        }

        if (control is not null)
            foreach (var (id, decoder) in BindControlDecoders(plugin))
                control.Register(id, decoder);

        if (compositor is not null)
            foreach (var (kind, factory) in BindLayerSurfaces(plugin))
                compositor.AddLayerSurface(kind, cfg =>
                    factory.Create(cfg) ?? throw new InvalidOperationException(
                        $"plugin layer surface '{kind}' returned no instance."));
    }

    internal static void ClearLastError() => t_lastErrorMessage = null;

    internal static InvalidOperationException StatusException(string operation, int status)
    {
        var detail = string.IsNullOrWhiteSpace(t_lastErrorMessage) ? string.Empty : $": {t_lastErrorMessage}";
        return new InvalidOperationException($"{operation} failed with status {status}{detail}");
    }

    internal static void ThrowIfError(string operation, int status)
    {
        if (status < 0 && status != (int)MfpStatus.ErrAgain && status != (int)MfpStatus.ErrEnd)
            throw StatusException(operation, status);
    }

    internal static void DestroyRegisteredCapabilities(IReadOnlyList<AbiRegisteredCapability> registered)
    {
        for (var i = registered.Count - 1; i >= 0; i--)
        {
            var cap = registered[i];
            var self = (void*)cap.Self;
            switch (cap.Capability)
            {
                case "audio-backend":
                    var audio = (MfpAudioBackendVTable*)cap.VTable;
                    if (audio->Destroy != null) audio->Destroy(self);
                    break;
                case "media-source-provider":
                    var media = (MfpMediaSourceProviderVTable*)cap.VTable;
                    if (media->Destroy != null) media->Destroy(self);
                    break;
                case "video-output":
                    var output = (MfpVideoOutputVTable*)cap.VTable;
                    if (output->Destroy != null) output->Destroy(self);
                    break;
                case "layer-surface":
                    var layer = (MfpLayerSurfaceFactoryVTable*)cap.VTable;
                    if (layer->Destroy != null) layer->Destroy(self);
                    break;
                case "subtitle-provider":
                    var subtitle = (MfpSubtitleProviderVTable*)cap.VTable;
                    if (subtitle->Destroy != null) subtitle->Destroy(self);
                    break;
                case "control-decoder":
                    var control = (MfpControlDecoderVTable*)cap.VTable;
                    if (control->Destroy != null) control->Destroy(self);
                    break;
            }
        }
    }

    internal static void FreeRegisteredVTables(IReadOnlyList<AbiRegisteredCapability> registered)
    {
        foreach (var cap in registered)
            foreach (var table in cap.OwnedVTables)
                NativeMemory.Free((void*)table);
    }

    private static int Record(void* ctx, byte* id, void* vt, void* self, string capability)
    {
        List<nint>? owned = null;
        try
        {
            var capabilityId = Utf8(id);
            if (string.IsNullOrWhiteSpace(capabilityId))
                throw new ArgumentException($"plugin registered {capability} with an empty id.");

            var normalized = NormalizeCapability(capability, vt, out owned);
            var state = (RegistrationState)GCHandle.FromIntPtr((nint)ctx).Target!;
            if (state.Capabilities.Any(c => c.Capability == capability
                                            && string.Equals(c.Id, capabilityId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"duplicate {capability} id '{capabilityId}'.");
            state.Capabilities.Add(new AbiRegisteredCapability(capability, capabilityId, normalized, (nint)self)
            {
                OwnedVTables = owned,
            });
            return (int)MfpStatus.Ok;
        }
        catch (Exception ex)
        {
            if (owned is not null)
                foreach (var p in owned)
                    NativeMemory.Free((void*)p);
            t_lastErrorMessage = ex.Message;
            if (ctx != null)
                ((RegistrationState)GCHandle.FromIntPtr((nint)ctx).Target!).Failure = ex.Message;
            return (int)MfpStatus.ErrAbiMismatch;
        }
    }

    private static nint NormalizeCapability(string capability, void* vt, out List<nint> owned)
    {
        owned = [];
        nint main;
        switch (capability)
        {
            case "audio-backend":
                main = NormalizeTable<MfpAudioBackendVTable>(vt, nameof(MfpAudioBackendVTable.Destroy), capability, owned);
                var audioBackend = (MfpAudioBackendVTable*)main;
                Require(audioBackend->OpenOutput != null || audioBackend->OpenInput != null,
                    "audio-backend must provide open_output or open_input");
                Require(audioBackend->CloseHandle != null, "audio-backend must provide close_handle");
                Require(audioBackend->OpenOutput == null || audioBackend->OutputSubmit != null,
                    "audio-backend open_output requires output_submit");
                Require(audioBackend->OpenInput == null || audioBackend->InputReadInto != null,
                    "audio-backend open_input requires input_read_into");
                break;
            case "video-output":
                main = NormalizeTable<MfpVideoOutputVTable>(vt, nameof(MfpVideoOutputVTable.Destroy), capability, owned);
                var videoOutput = (MfpVideoOutputVTable*)main;
                Require(videoOutput->AcceptedPixelFormats != null, "video-output must provide accepted_pixel_formats");
                Require(videoOutput->Configure != null, "video-output must provide configure");
                Require(videoOutput->Submit != null, "video-output must provide submit");
                Require(videoOutput->AcceptedFrameKinds != 0, "video-output must advertise accepted_frame_kinds");
                Require(videoOutput->AcceptedSyncKinds != 0, "video-output must advertise accepted_sync_kinds");
                break;
            case "control-decoder":
                main = NormalizeTable<MfpControlDecoderVTable>(vt, nameof(MfpControlDecoderVTable.Destroy), capability, owned);
                Require(((MfpControlDecoderVTable*)main)->Decode != null, "control-decoder must provide decode");
                break;
            case "media-source-provider":
            {
                main = NormalizeTable<MfpMediaSourceProviderVTable>(vt, nameof(MfpMediaSourceProviderVTable.Destroy), capability, owned);
                var provider = (MfpMediaSourceProviderVTable*)main;
                if (provider->VideoSourceVTable != null)
                {
                    provider->VideoSourceVTable = (MfpVideoSourceVTable*)NormalizeTable<MfpVideoSourceVTable>(
                        provider->VideoSourceVTable, nameof(MfpVideoSourceVTable.Destroy), "video-source", owned);
                    Require(provider->VideoSourceVTable->GetFormat != null, "video-source must provide get_format");
                    Require(provider->VideoSourceVTable->TryReadFrame != null, "video-source must provide try_read_frame");
                    Require(provider->VideoSourceVTable->ReleaseFrame != null, "video-source must provide release_frame");
                    Require(provider->VideoSourceVTable->Destroy != null, "video-source must provide destroy");
                    Require(provider->VideoSourceVTable->SupportedFrameKinds != 0,
                        "video-source must advertise supported_frame_kinds");
                    Require(provider->VideoSourceVTable->SupportedSyncKinds != 0,
                        "video-source must advertise supported_sync_kinds");
                }
                if (provider->AudioSourceVTable != null)
                {
                    provider->AudioSourceVTable = (MfpAudioSourceVTable*)NormalizeTable<MfpAudioSourceVTable>(
                        provider->AudioSourceVTable, nameof(MfpAudioSourceVTable.Destroy), "audio-source", owned);
                    Require(provider->AudioSourceVTable->NativeFormat != null, "audio-source must provide native_format");
                    Require(provider->AudioSourceVTable->ReadInto != null, "audio-source must provide read_into");
                    Require(provider->AudioSourceVTable->Destroy != null, "audio-source must provide destroy");
                }
                Require(provider->CanOpen != null, "media-source-provider must provide can_open");
                Require(provider->Open != null, "media-source-provider must provide open");
                Require(provider->VideoSourceVTable != null || provider->AudioSourceVTable != null,
                    "media-source-provider must expose a video or audio source vtable");
                break;
            }
            case "subtitle-provider":
            {
                main = NormalizeTable<MfpSubtitleProviderVTable>(vt, nameof(MfpSubtitleProviderVTable.Destroy), capability, owned);
                var provider = (MfpSubtitleProviderVTable*)main;
                if (provider->SubtitleVTable != null)
                {
                    provider->SubtitleVTable = (MfpSubtitleVTable*)NormalizeTable<MfpSubtitleVTable>(
                        provider->SubtitleVTable, nameof(MfpSubtitleVTable.Destroy), "subtitle", owned);
                    Require(provider->SubtitleVTable->RenderAt != null, "subtitle must provide render_at");
                    Require(provider->SubtitleVTable->ReleaseFrame != null, "subtitle must provide release_frame");
                    Require(provider->SubtitleVTable->Destroy != null, "subtitle must provide destroy");
                }
                Require(provider->CanOpen != null, "subtitle-provider must provide can_open");
                Require(provider->Open != null, "subtitle-provider must provide open");
                Require(provider->SubtitleVTable != null, "subtitle-provider must expose a subtitle vtable");
                break;
            }
            case "layer-surface":
            {
                main = NormalizeTable<MfpLayerSurfaceFactoryVTable>(vt, nameof(MfpLayerSurfaceFactoryVTable.Destroy), capability, owned);
                var factory = (MfpLayerSurfaceFactoryVTable*)main;
                if (factory->SurfaceVTable != null)
                {
                    factory->SurfaceVTable = (MfpLayerSurfaceVTable*)NormalizeTable<MfpLayerSurfaceVTable>(
                        factory->SurfaceVTable, nameof(MfpLayerSurfaceVTable.Destroy), "layer-surface-instance", owned);
                    Require(factory->SurfaceVTable->ConfigureGl != null, "layer-surface must provide configure_gl");
                    Require(factory->SurfaceVTable->Render != null, "layer-surface must provide render");
                    Require(factory->SurfaceVTable->Destroy != null, "layer-surface must provide destroy");
                }
                Require(factory->Create != null, "layer-surface factory must provide create");
                Require(factory->SurfaceVTable != null, "layer-surface factory must expose a surface vtable");
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown ABI capability.");
        }
        return main;
    }

    private static nint NormalizeTable<T>(void* source, string lastRequiredField, string label, List<nint> owned)
        where T : unmanaged
    {
        if (source == null)
            throw new ArgumentException($"{label} vtable is null.");
        var header = (MfpVTableHeader*)source;
        ValidateVersionAndSize(header->AbiVersion, header->StructSize, RequiredSize<T>(lastRequiredField), $"{label} vtable");

        var size = (nuint)sizeof(T);
        var destination = NativeMemory.AllocZeroed(size);
        Buffer.MemoryCopy(source, destination, size, Math.Min(size, header->StructSize));
        owned.Add((nint)destination);
        return (nint)destination;
    }

    private static uint RequiredSize<T>(string field) where T : unmanaged =>
        checked((uint)Marshal.OffsetOf<T>(field).ToInt64() + (uint)IntPtr.Size);

    private static void ValidateVersionAndSize(uint version, uint size, uint requiredSize, string label)
    {
        if ((version >> 16) != (AbiVersion >> 16))
            throw new InvalidOperationException(
                $"{label} reports ABI {version >> 16}.{version & 0xffff}, host is {AbiVersion >> 16}.{AbiVersion & 0xffff}.");
        if (size < requiredSize)
            throw new InvalidOperationException($"{label} size {size} is smaller than required size {requiredSize}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [UnmanagedCallersOnly]
    private static int AddAudioBackend(void* ctx, byte* id, void* vt, void* self) =>
        Record(ctx, id, vt, self, "audio-backend");

    [UnmanagedCallersOnly]
    private static int AddMediaSourceProvider(void* ctx, byte* id, void* vt, void* self) =>
        Record(ctx, id, vt, self, "media-source-provider");

    [UnmanagedCallersOnly]
    private static int AddVideoOutput(void* ctx, byte* id, void* vt, void* self) =>
        Record(ctx, id, vt, self, "video-output");

    [UnmanagedCallersOnly]
    private static int AddLayerSurface(void* ctx, byte* id, void* vt, void* self) =>
        Record(ctx, id, vt, self, "layer-surface");

    [UnmanagedCallersOnly]
    private static int AddSubtitleProvider(void* ctx, byte* id, void* vt, void* self) =>
        Record(ctx, id, vt, self, "subtitle-provider");

    [UnmanagedCallersOnly]
    private static int AddControlDecoder(void* ctx, byte* id, void* vt, void* self) =>
        Record(ctx, id, vt, self, "control-decoder");

    [UnmanagedCallersOnly]
    private static void HostLog(int level, byte* msg) => LastLogMessage = Utf8(msg);

    [UnmanagedCallersOnly]
    private static void HostSetLastError(byte* msg) => t_lastErrorMessage = Utf8(msg);

    [UnmanagedCallersOnly]
    private static long HostNowTicks() =>
        (long)(Stopwatch.GetTimestamp() * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

    private static string Utf8(byte* p) =>
        p == null ? string.Empty : Marshal.PtrToStringUTF8((nint)p) ?? string.Empty;

    private static uint SupportedFrameKinds()
    {
        var kinds = 1u << (int)MfpFrameKind.Cpu;
        if (OperatingSystem.IsLinux())
            kinds |= 1u << (int)MfpFrameKind.DmaBuf;
        if (OperatingSystem.IsWindows())
            kinds |= 1u << (int)MfpFrameKind.D3D11;
        return kinds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MfpVTableHeader
    {
        public uint AbiVersion;
        public uint StructSize;
    }

    private sealed class RegistrationState
    {
        public List<AbiRegisteredCapability> Capabilities { get; } = [];
        public string? Failure { get; set; }
    }
}

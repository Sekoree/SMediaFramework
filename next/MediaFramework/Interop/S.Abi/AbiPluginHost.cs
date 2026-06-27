using System.Runtime.InteropServices;
using S.Control;

namespace S.Abi;

/// <summary>A capability a loaded plugin registered: its kind + the id/scheme it registered under, plus the raw
/// vtable + instance pointers a managed adapter binds to.</summary>
public sealed record AbiRegisteredCapability(string Capability, string Id, nint VTable, nint Self);

/// <summary>A loaded native plugin: its identity, advertised capability bitset, and the capabilities it actually
/// registered. Owns the library handle — dispose to unload (only after every adapter built from it is gone).</summary>
public sealed class AbiLoadedPlugin : IDisposable
{
    private nint _lib;

    internal AbiLoadedPlugin(nint lib, string id, string displayName, uint capabilities,
        IReadOnlyList<AbiRegisteredCapability> registered)
    {
        _lib = lib;
        Id = id;
        DisplayName = displayName;
        Capabilities = capabilities;
        Registered = registered;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public uint Capabilities { get; }
    public IReadOnlyList<AbiRegisteredCapability> Registered { get; }

    public void Dispose()
    {
        if (_lib != 0)
        {
            NativeLibrary.Free(_lib);
            _lib = 0;
        }
    }
}

/// <summary>
/// The inbound native plugin host (S.Abi, Tier B). Loads a shared library, calls its <c>mfp_plugin_register</c>
/// entry point with a host-API + a registrar, and records every capability the plugin registers. All of
/// NativeLibrary.Load/GetExport + <c>delegate* unmanaged</c> + <c>[UnmanagedCallersOnly]</c> is NativeAOT-safe
/// (no reflection). Binding each recorded vtable to a managed framework interface and registering it into a scoped
/// registry (media/compositor/control) is the next layer built on this.
/// </summary>
public static unsafe class AbiPluginHost
{
    public const uint AbiVersion = 1u;

    public static AbiLoadedPlugin Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var lib = NativeLibrary.Load(path);
        try
        {
            var register = (delegate* unmanaged<MfpHostApi*, MfpPluginInfo*, MfpRegistrar*, int>)
                NativeLibrary.GetExport(lib, "mfp_plugin_register");

            var recorded = new List<AbiRegisteredCapability>();
            var ctx = GCHandle.Alloc(recorded);
            try
            {
                var host = new MfpHostApi
                {
                    AbiVersion = AbiVersion,
                    Log = &HostLog,
                    SetLastError = &HostSetLastError,
                    NowTicks = &HostNowTicks,
                    AllocFrame = &HostAllocFrame,
                    ReleaseFrame = &HostReleaseFrame,
                };
                var reg = new MfpRegistrar
                {
                    AbiVersion = AbiVersion,
                    Ctx = (void*)GCHandle.ToIntPtr(ctx),
                    AddAudioBackend = &AddAudioBackend,
                    AddMediaSourceProvider = &AddMediaSourceProvider,
                    AddVideoOutput = &AddVideoOutput,
                    AddLayerSurface = &AddLayerSurface,
                    AddSubtitleProvider = &AddSubtitleProvider,
                    AddControlDecoder = &AddControlDecoder,
                };

                MfpPluginInfo info = default;
                var rc = register(&host, &info, &reg);
                if (rc != (int)MfpStatus.Ok)
                    throw new InvalidOperationException($"mfp_plugin_register('{path}') returned {rc}.");
                if (info.AbiVersion != AbiVersion)
                    throw new InvalidOperationException(
                        $"plugin '{path}' reports ABI {info.AbiVersion}, host is {AbiVersion}.");

                var plugin = new AbiLoadedPlugin(
                    lib, Utf8(info.Id), Utf8(info.DisplayName), info.Capabilities, recorded);
                lib = 0; // ownership transferred to the returned plugin
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
                NativeLibrary.Free(lib);
        }
    }

    /// <summary>Binds a loaded plugin's registered control-decoder capabilities to managed
    /// <see cref="IControlMeterBlobDecoder"/> adapters (id → decoder). Register each into a
    /// <see cref="ControlMeterBlobDecoderRegistry"/> so a device profile can reference it by name.</summary>
    public static IReadOnlyList<(string Id, IControlMeterBlobDecoder Decoder)> BindControlDecoders(AbiLoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var result = new List<(string, IControlMeterBlobDecoder)>();
        foreach (var cap in plugin.Registered)
            if (cap.Capability == "control-decoder")
                result.Add((cap.Id, new NativeControlDecoder(cap.VTable, cap.Self)));
        return result;
    }

    /// <summary>Binds a loaded plugin's registered media-source-provider capabilities to
    /// <see cref="NativeMediaSourceProvider"/> adapters (scheme → provider). Open a URI through one to get a managed
    /// <see cref="S.Media.Core.Video.IVideoSource"/>.</summary>
    public static IReadOnlyList<(string Scheme, NativeMediaSourceProvider Provider)> BindMediaSourceProviders(AbiLoadedPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var result = new List<(string, NativeMediaSourceProvider)>();
        foreach (var cap in plugin.Registered)
            if (cap.Capability == "media-source-provider")
                result.Add((cap.Id, new NativeMediaSourceProvider(cap.VTable, cap.Self)));
        return result;
    }

    private static int Record(void* ctx, byte* id, void* vt, void* self, string capability)
    {
        var list = (List<AbiRegisteredCapability>)GCHandle.FromIntPtr((nint)ctx).Target!;
        list.Add(new AbiRegisteredCapability(capability, Utf8(id), (nint)vt, (nint)self));
        return (int)MfpStatus.Ok;
    }

    [UnmanagedCallersOnly] private static int AddAudioBackend(void* ctx, byte* id, void* vt, void* self) => Record(ctx, id, vt, self, "audio-backend");
    [UnmanagedCallersOnly] private static int AddMediaSourceProvider(void* ctx, byte* id, void* vt, void* self) => Record(ctx, id, vt, self, "media-source-provider");
    [UnmanagedCallersOnly] private static int AddVideoOutput(void* ctx, byte* id, void* vt, void* self) => Record(ctx, id, vt, self, "video-output");
    [UnmanagedCallersOnly] private static int AddLayerSurface(void* ctx, byte* id, void* vt, void* self) => Record(ctx, id, vt, self, "layer-surface");
    [UnmanagedCallersOnly] private static int AddSubtitleProvider(void* ctx, byte* id, void* vt, void* self) => Record(ctx, id, vt, self, "subtitle-provider");
    [UnmanagedCallersOnly] private static int AddControlDecoder(void* ctx, byte* id, void* vt, void* self) => Record(ctx, id, vt, self, "control-decoder");

    [UnmanagedCallersOnly] private static void HostLog(int level, byte* msg) { }
    [UnmanagedCallersOnly] private static void HostSetLastError(byte* msg) { }
    [UnmanagedCallersOnly] private static long HostNowTicks() => DateTime.UtcNow.Ticks;
    [UnmanagedCallersOnly] private static void* HostAllocFrame(MfpVideoFormat* fmt) => null;
    [UnmanagedCallersOnly] private static void HostReleaseFrame(void* frame) { }

    private static string Utf8(byte* p) =>
        p == null ? string.Empty : Marshal.PtrToStringUTF8((nint)p) ?? string.Empty;
}

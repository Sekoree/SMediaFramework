using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Compositor;
using S.Media.Compositor.OpenGL;
using S.Media.Gpu;
using SilkGL = Silk.NET.OpenGL.GL;

namespace S.Media.Present.SDL3;

/// <summary>
/// Framework-level OpenGL compositor host backed by a hidden SDL3 window/context.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GlVideoCompositor"/> needs a current GL context on the same thread that calls
/// <see cref="Composite"/> and <see cref="Dispose"/>. This host owns that hidden context while still
/// exposing the normal <see cref="IVideoCompositor"/> contract, so app-level runtimes can use GPU
/// composition without creating their own off-screen GL window.
/// </para>
/// <para>
/// Output formats are CPU-readable <see cref="PixelFormat.Bgra32"/>, <see cref="PixelFormat.Rgba16"/>,
/// or <see cref="PixelFormat.Rgba16F"/> frames. SDL/Avalonia outputs can render those frames directly;
/// NDI can consume the BGRA32 path through its existing sender.
/// </para>
/// </remarks>
public sealed class SDL3GLVideoCompositor : IWarpPassVideoCompositor, IVideoCompositorSurfaceHost
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Present.SDL3.SDL3GLVideoCompositor");
    private static readonly PixelFormat[] AcceptedFormats = YuvVideoRenderer.SupportedPixelFormats.ToArray();

    private VideoFormat _output;
    private GlVideoCompositor? _inner;
    private SharedSDLGlContext? _context;
    private int _ownerThreadId;
    private bool _disposeRequested;
    private bool _resourcesDisposed;

    public SDL3GLVideoCompositor(VideoFormat output)
    {
        ValidateOutputFormat(output);
        _output = output;
    }

    public VideoFormat OutputFormat => _output;

    public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => AcceptedFormats;

    public static bool TryCreate(VideoFormat output, out IVideoCompositor? compositor, out string? error)
    {
        compositor = null;
        if (!IsSupportedOutputFormat(output.PixelFormat))
        {
            error = $"SDL3 GL compositor cannot output {output.PixelFormat}; supported outputs: Bgra32, Rgba16, Rgba16F.";
            return false;
        }

        if (!TryProbe(output.PixelFormat, out error))
            return false;

        compositor = new SDL3GLVideoCompositor(output);
        return true;
    }

    public static bool TryProbe(out string? error) => TryProbe(PixelFormat.Bgra32, out error);

    public static bool TryProbe(PixelFormat outputPixelFormat, out string? error)
    {
        error = null;
        if (!IsSupportedOutputFormat(outputPixelFormat))
        {
            error = $"Unsupported GL compositor output pixel format {outputPixelFormat}.";
            return false;
        }

        nint window = 0;
        nint glContext = 0;
        SilkGL? gl = null;
        GlVideoCompositor? probeCompositor = null;
        var acquired = false;
        try
        {
            SDL3Runtime.Acquire();
            acquired = true;
            ApplyGlAttributes();
            window = SDL.CreateWindow(
                "S.Media SDL3 GL Compositor Probe",
                16,
                16,
                SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
            if (window == 0)
                throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");

            glContext = SDL.GLCreateContext(window);
            if (glContext == 0)
                throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.GetError()}");
            if (!SDL.GLMakeCurrent(window, glContext))
                throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {SDL.GetError()}");

            gl = SilkGL.GetApi(SDL.GLGetProcAddress);
            var probeFormat = new VideoFormat(16, 16, outputPixelFormat, new Rational(60, 1));
            probeCompositor = new GlVideoCompositor(gl, probeFormat, PrecisionForOutput(outputPixelFormat));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { probeCompositor?.Dispose(); } catch { /* best effort */ }
            try { gl?.Dispose(); } catch { /* best effort */ }
            if (glContext != 0)
            {
                try { SDL.GLDestroyContext(glContext); } catch { /* best effort */ }
            }
            if (window != 0)
            {
                try { SDL.DestroyWindow(window); } catch { /* best effort */ }
            }
            if (acquired)
            {
                try { SDL3Runtime.Release(); } catch { /* best effort */ }
            }
        }
    }

    public void Configure(VideoFormat output)
    {
        ValidateOutputFormat(output);
        if (_inner is not null
            && PrecisionForOutput(output.PixelFormat) != PrecisionForOutput(_output.PixelFormat))
        {
            throw new InvalidOperationException(
                $"Cannot change SDL3 GL compositor output precision from {_output.PixelFormat} to {output.PixelFormat} after initialization.");
        }

        _output = output;
        if (_inner is not null)
        {
            // _inner.Configure recreates GL FBOs - it must run against our own context, which another
            // compositor sharing this thread may have displaced (see EnsureContextCurrent remarks).
            EnsureContextCurrent();
            _inner.Configure(output);
        }
    }

    public VideoFrame Composite(IReadOnlyList<CompositorLayer> layersBackToFront, TimeSpan presentationTime)
    {
        if (_disposeRequested)
            throw new ObjectDisposedException(nameof(SDL3GLVideoCompositor));
        EnsureInitialized();
        EnsureContextCurrent();
        return _inner!.Composite(layersBackToFront, presentationTime);
    }

    public IReadOnlyList<VideoFrame> CompositeMulti(
        IReadOnlyList<CompositorLayer> layersBackToFront,
        IReadOnlyList<WarpOutputRequest> outputs,
        TimeSpan presentationTime)
    {
        if (_disposeRequested)
            throw new ObjectDisposedException(nameof(SDL3GLVideoCompositor));
        EnsureInitialized();
        EnsureContextCurrent();
        return _inner!.CompositeMulti(layersBackToFront, outputs, presentationTime);
    }

    /// <summary>Surface-host capability (NXT-10) - delegates to the inner <see cref="GlVideoCompositor"/>
    /// with this host's context current, exactly like <see cref="Composite"/>. Without this the app's
    /// SDL3-driven compositions (the deck/cue default) reported no surface support, and surface-capable
    /// sources (MMD) silently fell back to their CPU frame path (the 2026-07-03 grayscale-MMD report).</summary>
    public VideoFrame CompositeWithSurfaces(
        IReadOnlyList<CompositorLayer> frameLayers,
        IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
        TimeSpan presentationTime)
    {
        if (_disposeRequested)
            throw new ObjectDisposedException(nameof(SDL3GLVideoCompositor));
        EnsureInitialized();
        EnsureContextCurrent();
        return _inner!.CompositeWithSurfaces(frameLayers, surfaceLayers, presentationTime);
    }

    public void CompositeMultiToTargets(
        IReadOnlyList<CompositorLayer> layersBackToFront,
        IReadOnlyList<TargetedWarpOutput> targets,
        TimeSpan presentationTime)
    {
        if (_disposeRequested)
            throw new ObjectDisposedException(nameof(SDL3GLVideoCompositor));
        EnsureInitialized();
        EnsureContextCurrent();
        _inner!.CompositeMultiToTargets(layersBackToFront, targets, presentationTime);
    }

    /// <summary>
    /// Re-asserts the shared GL context as current on the calling thread before any GL work. Compositors
    /// on one thread now share a single <see cref="SharedSDLGlContext"/>, so sibling compositors can no
    /// longer displace each other's binding; this guards only against an unrelated GL user (e.g. the
    /// <see cref="TryProbe(out string)"/> transient context) leaving a different context current.
    /// </summary>
    private void EnsureContextCurrent() => _context?.MakeCurrent();

    private readonly object _warpGate = new();
    private VideoFormat _pendingWarpOutput;
    private IReadOnlyList<WarpSection>? _pendingWarpSections;
    private bool _hasPendingWarp;

    /// <inheritdoc />
    public void SetWarpPass(VideoFormat warpOutput, IReadOnlyList<WarpSection>? sections)
    {
        // The inner compositor is created lazily on the pump thread - buffer the warp config until
        // then. The inner setter itself is a GL-free snapshot swap, so forwarding from any thread
        // is safe once it exists.
        lock (_warpGate)
        {
            _pendingWarpOutput = warpOutput;
            _pendingWarpSections = sections;
            _hasPendingWarp = true;
            _inner?.SetWarpPass(warpOutput, sections);
        }
    }

    /// <summary>
    /// Disposes GL resources only when called on the context owner thread.
    /// Use this from a composition pump's finally block before disposing from another thread.
    /// </summary>
    public void DisposeOnOwnerThread()
    {
        if (_ownerThreadId != 0 && Environment.CurrentManagedThreadId != _ownerThreadId)
            return;
        DisposeCore();
    }

    public void Dispose()
    {
        _disposeRequested = true;
        if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId == _ownerThreadId)
            DisposeCore();
    }

    private void EnsureInitialized()
    {
        if (_inner is not null)
            return;

        _ownerThreadId = Environment.CurrentManagedThreadId;

        // Share one GL context with every other compositor on this thread (the canvas mixer and its
        // mapping stages). The context is reference-counted and made current here; subsequent Composite
        // calls re-assert it (EnsureContextCurrent) so a probe or sibling compositor can't displace it.
        _context = SharedSDLGlContext.Acquire();
        _context.MakeCurrent();

        _inner = new GlVideoCompositor(_context.Gl, _output, PrecisionForOutput(_output.PixelFormat));
        lock (_warpGate)
        {
            if (_hasPendingWarp)
                _inner.SetWarpPass(_pendingWarpOutput, _pendingWarpSections);
        }
        Trace.LogInformation("SDL3 GL compositor initialized for {Width}x{Height} {PixelFormat} {RateNum}/{RateDen}",
            _output.Width,
            _output.Height,
            _output.PixelFormat,
            _output.FrameRate.Numerator,
            _output.FrameRate.Denominator);
    }

    private void DisposeCore()
    {
        if (_resourcesDisposed)
            return;
        _resourcesDisposed = true;
        _disposeRequested = true;

        // Delete our GL objects against the shared context, then drop our reference to it. The context
        // (and the SDL video subsystem ref it holds) is torn down only when the last compositor releases.
        var context = _context;
        _context = null;
        try { context?.MakeCurrent(); }
        catch (Exception ex) { Trace.LogWarning(ex, "SDL3GLVideoCompositor.Dispose: make context current"); }

        try { _inner?.Dispose(); }
        catch (Exception ex) { Trace.LogWarning(ex, "SDL3GLVideoCompositor.Dispose: GL compositor"); }
        _inner = null;

        try { context?.Release(); }
        catch (Exception ex) { Trace.LogWarning(ex, "SDL3GLVideoCompositor.Dispose: shared context release"); }
    }

    private static void ValidateOutputFormat(VideoFormat output)
    {
        if (!IsSupportedOutputFormat(output.PixelFormat))
            throw new ArgumentException(
                $"SDL3 GL compositor output must be Bgra32, Rgba16, or Rgba16F; got {output.PixelFormat}.",
                nameof(output));
    }

    private static bool IsSupportedOutputFormat(PixelFormat pixelFormat) =>
        pixelFormat is PixelFormat.Bgra32 or PixelFormat.Rgba16 or PixelFormat.Rgba16F;

    private static GlCompositorOutputPrecision PrecisionForOutput(PixelFormat pixelFormat) => pixelFormat switch
    {
        PixelFormat.Rgba16 => GlCompositorOutputPrecision.Rgba16,
        PixelFormat.Rgba16F => GlCompositorOutputPrecision.Rgba16F,
        _ => GlCompositorOutputPrecision.Rgba8,
    };

    internal static void ApplyGlAttributes()
    {
        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);
        SDL.GLSetAttribute(SDL.GLAttr.RedSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.GreenSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.BlueSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.AlphaSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.DepthSize, 0);
    }
}

// The old global MediaFrameworkRuntime registration extension (UseSDL3OpenGLCompositor) is removed (P2 -
// no process-wide runtime). Wire SDL3GLVideoCompositor.TryCreate as a VideoCompositorBackendFactory via
// per-session VideoCompositorOptions.AutoBackends, or VideoCompositor.RegisterAutoBackend at a composition root.

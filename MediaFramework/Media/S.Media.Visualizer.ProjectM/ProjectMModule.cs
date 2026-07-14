using ProjectMLib;

namespace S.Media.Visualizer.ProjectM;

/// <summary>
/// Registers the "projectm" visual-source kind into a bus registry (and, when given one, the
/// compositor registry's layer-surface kinds for config-blob-driven cue layers). Call only when
/// <see cref="IsAvailable"/> - the host composition root gates it exactly like the NDI module.
/// </summary>
public static class ProjectMModule
{
    public const string Kind = "projectm";

    public static bool IsAvailable => ProjectMRuntime.IsAvailable;

    public static string? UnavailableReason => ProjectMRuntime.UnavailableReason;

    public static string? Version => ProjectMRuntime.Version;

    public static IBusRegistryBuilder Register(IBusRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddVisualSource(Kind, args => new ProjectMVisualSource(
            args.Width, args.Height, args.FrameRate, ProjectMOptions.FromJson(args.ConfigJson)));
    }

    public static Compositor.ICompositorRegistryBuilder Register(Compositor.ICompositorRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddLayerSurface(Kind, configJson =>
        {
            var options = ProjectMOptions.FromJson(configJson);
            var source = new ProjectMVisualSource(1920, 1080, new Rational(30, 1), options);
            try
            {
                return new SourceOwnedLayerSurface(source, source.CreateLayerSurface());
            }
            catch
            {
                source.Dispose();
                throw;
            }
        });
    }

    /// <summary>The registry creates a source solely to back one surface, so the returned surface must
    /// carry source ownership. Without this adapter every composition rebuild leaked a renderer thread.</summary>
    private sealed class SourceOwnedLayerSurface(
        ProjectMVisualSource source,
        Compositor.IVideoCompositorLayerSurface inner) :
        Compositor.IVideoCompositorLayerSurface,
        Compositor.IVideoCompositorGlResource
    {
        private int _disposed;

        public void ConfigureGl(Silk.NET.OpenGL.GL gl, VideoFormat canvas) => inner.ConfigureGl(gl, canvas);

        public void Render(
            Silk.NET.OpenGL.GL gl,
            uint targetFbo,
            TimeSpan masterTime,
            Compositor.LayerTransform2D transform,
            float opacity) => inner.Render(gl, targetFbo, masterTime, transform, opacity);

        public void ReleaseGl(Silk.NET.OpenGL.GL gl)
        {
            if (inner is Compositor.IVideoCompositorGlResource resource)
                resource.ReleaseGl(gl);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            inner.Dispose();
            source.Dispose();
        }
    }
}

using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace S.Media.Compositor;

/// <summary>
/// Wraps an <see cref="IVideoSource"/> and converts each decoded frame to a fixed
/// <paramref name="targetPixelFormat"/> via an injected CPU converter factory
/// (registry-wired, <c>IMediaRegistry.CreateCpuConverter</c> — P3, no direct FFmpeg dependency).
/// </summary>
/// <remarks>
/// Used for live NDI (UYVY) into local SDL/Avalonia outputs that are most reliable on BGRA32.
/// </remarks>
public sealed class PixelFormatConvertingVideoSource : IVideoSource, ICooperativeVideoReadInterrupt, IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Video.PixelFormatConvertingVideoSource");
    private static int _firstConvertLogged;

    private readonly IVideoSource _inner;
    private readonly PixelFormat _target;
    private readonly bool _disposeInner;
    private readonly Func<IVideoCpuFrameConverter>? _cpuConverterFactory;
    private IVideoCpuFrameConverter? _converter;
    private VideoFormat? _format;
    private bool _disposed;

    public PixelFormatConvertingVideoSource(
        IVideoSource inner,
        PixelFormat targetPixelFormat,
        bool disposeInner = false,
        Func<IVideoCpuFrameConverter>? cpuConverterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _target = targetPixelFormat;
        _disposeInner = disposeInner;
        _cpuConverterFactory = cpuConverterFactory;
    }

    public VideoFormat Format => _format ??= ResolveFormat();

    public IReadOnlyList<PixelFormat> NativePixelFormats => [_target];

    public bool IsExhausted => _disposed || _inner.IsExhausted;

    public void SelectOutputFormat(PixelFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (format != _target)
            throw new InvalidOperationException(
                $"{nameof(PixelFormatConvertingVideoSource)} delivers {_target} only; requested {format}.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default!;
        if (!_inner.TryReadNextFrame(out var src))
            return false;

        if (src.Format.PixelFormat == _target)
        {
            _format ??= src.Format;
            frame = src;
            return true;
        }

        var w = src.Format.Width;
        var h = src.Format.Height;
        if (_cpuConverterFactory is null)
        {
            src.Dispose();
            return false;
        }

        var srcPf = src.Format.PixelFormat;
        var srcRange = src.ColorRange;
        try
        {
            _converter ??= _cpuConverterFactory();
            _converter.Configure(srcPf, _target, w, h);
        }
        catch
        {
            // Unsupported source format for this converter — skip (the old CanConvert==false path).
            src.Dispose();
            return false;
        }

        try
        {
            var converted = _converter.Convert(src, src.ColorTransferHint);
            src.Dispose();
            _format = converted.Format;
            frame = converted;
            if (Interlocked.Increment(ref _firstConvertLogged) <= 2 && Trace.IsEnabled(LogLevel.Information))
            {
                Trace.LogInformation(
                    "PixelFormatConvertingVideoSource: {Src} → {Dst} ({W}x{H}) srcRange={SrcRange} dstRange={DstRange}",
                    srcPf, _target, w, h, srcRange, converted.ColorRange);
            }

            return true;
        }
        catch
        {
            src.Dispose();
            throw;
        }
    }

    public void RequestYieldBetweenReads()
    {
        if (_inner is ICooperativeVideoReadInterrupt interrupt)
            interrupt.RequestYieldBetweenReads();
    }

    public void ClearYieldRequest()
    {
        if (_inner is ICooperativeVideoReadInterrupt interrupt)
            interrupt.ClearYieldRequest();
    }

    private VideoFormat ResolveFormat()
    {
        var inner = _inner.Format;
        for (var i = 0; i < _inner.NativePixelFormats.Count; i++)
        {
            if (_inner.NativePixelFormats[i] == _target)
                return inner with { PixelFormat = _target };
        }

        return new VideoFormat(inner.Width, inner.Height, _target, inner.FrameRate);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _converter?.Dispose(); }
        catch { /* best effort */ }
        _converter = null;
        if (_disposeInner && _inner is IDisposable d)
        {
            try { d.Dispose(); }
            catch { /* best effort */ }
        }
    }
}

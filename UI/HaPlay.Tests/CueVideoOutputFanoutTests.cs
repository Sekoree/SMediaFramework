using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HaPlay.Models;
using HaPlay.Playback;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Session;
using Xunit;

namespace HaPlay.Tests;

/// <summary>NXT-06 slice #1: proves the cue-workspace ShowSession re-back fans a cue's video out to the
/// host-acquired video outputs (the GUI's NDI/SDL/local lines), via the composition-id-keyed video factory that
/// <c>MainViewModel.ReloadCueShowSession</c> wires from <c>OutputManagement.AcquireVideoOutputForLine</c>. The
/// real outputs need hardware; here a counting output stands in for an acquired line.</summary>
public sealed class CueVideoOutputFanoutTests
{
    [Fact]
    public async Task ImplicitLayoutTile_IsActiveBeforeFirstSave_AndLiveMoveKeepsOutputRaster()
    {
        var compId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var bindingId = Guid.NewGuid();
        var cue = new MediaCueNode
        {
            Label = "V",
            Source = new FilePlaylistItem("synthetic://v"),
            VideoPlacements = { new CueVideoPlacement { CompositionId = compId, LayerIndex = 0 } },
        };
        var cueList = new CueList
        {
            Nodes = { cue },
            Compositions = { new CueComposition { Id = compId, Name = "screen", Width = 24, Height = 24, FrameRateNum = 30 } },
            VideoOutputs =
            {
                new CueVideoOutputBinding
                {
                    Id = bindingId, CompositionId = compId, OutputLineId = lineId, MappingEnabled = true,
                },
            },
        };
        OutputDefinition definition = new LocalVideoOutputDefinition(
            lineId, "Program", VideoOutputEngine.SDLOpenGl, VideoSurfaceMode.Windowed,
            ScreenIndex: 0, WindowWidth: 16, WindowHeight: 16);
        var effective = HaPlayShowMapper.ResolveEffectiveVideoOutputMappings(cueList, [definition]);
        var initialMapping = Assert.IsType<CueOutputMapping>(effective[bindingId]);
        var screen = new CountingVideoOutput();
        await using var session = new ShowSession(
            MediaRegistry.Build(b => b.AddDecoder(new SyntheticVideoProvider())),
            videoOutputFactory: (_, name, _, _) =>
                [new ClipCompositionOutputLease(
                    lineId.ToString("N"), name, screen,
                    Mapping: HaPlayShowMapper.ToClipOutputMapping(initialMapping))]);
        session.LoadDocument(HaPlayShowMapper.ToShowDocument(cueList));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync(cue.Id.ToString()));
        // Wait for the EXPECTED CONTENT, not merely a frame count: the composition may legitimately
        // submit an initial black canvas before the synthetic source becomes visible, so a count-only
        // wait raced the first real frame under load (review P2-1).
        Assert.True(
            await WaitUntilAsync(
                () =>
                {
                    var farEdge = screen.BottomRight;
                    return screen.Count > 0
                           && farEdge.R is >= 130 and <= 210
                           && farEdge.B is >= 130 and <= 210;
                },
                TimeSpan.FromSeconds(3)),
            $"synthetic source never became visible; last far edge: {screen.BottomRight}");
        Assert.Equal((16, 16), (screen.Format.Width, screen.Format.Height));
        var initialFarEdge = screen.BottomRight;
        Assert.InRange(initialFarEdge.R, (byte)130, (byte)210);
        Assert.InRange(initialFarEdge.B, (byte)130, (byte)210);

        var moved = initialMapping with
        {
            Sections =
            [
                initialMapping.Sections[0] with { SrcX = 1d / 3, SrcY = 1d / 3 },
            ],
        };
        Assert.True(await session.ApplyOutputMappingAsync(
            compId.ToString(), lineId.ToString("N"), HaPlayShowMapper.ToClipOutputMapping(moved)));
        // Same content-based wait: a frame submitted before the move committed must not satisfy it.
        Assert.True(
            await WaitUntilAsync(
                () =>
                {
                    var nearEdge = screen.TopLeft;
                    var farEdge = screen.BottomRight;
                    return nearEdge.R is >= 45 and <= 125 && nearEdge.B is >= 45 and <= 125
                           && farEdge.R >= 220 && farEdge.B >= 220;
                },
                TimeSpan.FromSeconds(3)),
            $"moved mapping never became visible; last near/far edges: {screen.TopLeft} / {screen.BottomRight}");
        Assert.Equal((16, 16), (screen.Format.Width, screen.Format.Height));
        var movedNearEdge = screen.TopLeft;
        var movedFarEdge = screen.BottomRight;
        Assert.InRange(movedNearEdge.R, (byte)45, (byte)125);
        Assert.InRange(movedNearEdge.B, (byte)45, (byte)125);
        Assert.InRange(movedFarEdge.R, (byte)220, byte.MaxValue);
        Assert.InRange(movedFarEdge.B, (byte)220, byte.MaxValue);
    }

    [Fact]
    public async Task CueVideo_FansOutToHostAcquiredOutputs_ViaTheCompositionIdFactory()
    {
        var compId = Guid.NewGuid();
        var cue = new MediaCueNode
        {
            Label = "V",
            Source = new FilePlaylistItem("synthetic://v"),
            VideoPlacements = { new CueVideoPlacement { CompositionId = compId, LayerIndex = 0 } },
        };
        var cueList = new CueList
        {
            Name = "Show",
            Nodes = { cue },
            Compositions =
            {
                new CueComposition
                {
                    Id = compId, Name = "screen", Width = 16, Height = 16, FrameRateNum = 30, FrameRateDen = 1,
                },
            },
        };
        var doc = HaPlayShowMapper.ToShowDocument(cueList);

        // The counting output stands in for an NDI/SDL/local line; the factory keys on the composition id EXACTLY
        // as MainViewModel.ReloadCueShowSession wires _cueVideoOutputs → leases (borrowed host outputs, so the
        // lease declares DisposeOutputOnRuntimeDispose=false - the session must not dispose them, NXT-01).
        var screen = new CountingVideoOutput();
        var cueVideoOutputs = new Dictionary<string, IVideoOutput[]> { [compId.ToString()] = [screen] };
        await using var session = new ShowSession(
            MediaRegistry.Build(b => b.AddDecoder(new SyntheticVideoProvider())),
            videoOutputFactory: (cid, name, _, _) => cueVideoOutputs.TryGetValue(cid, out var outs)
                ? outs.Select((o, i) => new ClipCompositionOutputLease(
                    $"{cid}_out{i}", name, o, DisposeOutputOnRuntimeDispose: false)).ToArray()
                : Array.Empty<ClipCompositionOutputLease>());
        session.LoadDocument(doc);

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync(cue.Id.ToString()));

        var delivered = await WaitUntilAsync(() => screen.Count > 0, TimeSpan.FromSeconds(5));
        Assert.True(delivered, "cue video did not fan out to the host-acquired output");
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, (long)timeout.TotalMilliseconds);
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>A registry decoder that opens any URI as a short synthetic 16×16 BGRA video clip (no FFmpeg).</summary>
    private sealed class SyntheticVideoProvider : IMediaDecoderProvider
    {
        public string Name => "synthetic";
        public double Probe(string uri, MediaKind kind) => kind == MediaKind.Video ? 1.0 : 0.0;
        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new SyntheticVideoSource();
        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
            throw new NotSupportedException("synthetic provider is video-only");
    }

    private sealed class SyntheticVideoSource : IVideoSource
    {
        private const int FrameCount = 30;
        private int _next;

        public VideoFormat Format { get; } = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
        public bool IsExhausted => Volatile.Read(ref _next) >= FrameCount;

        public void SelectOutputFormat(PixelFormat format)
        {
            if (format != PixelFormat.Bgra32)
                throw new NotSupportedException();
        }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var index = Interlocked.Increment(ref _next) - 1;
            if (index >= FrameCount)
            {
                frame = null!;
                return false;
            }

            var pixels = new byte[16 * 16 * 4];
            for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var offset = (y * 16 + x) * 4;
                pixels[offset] = (byte)(255 * y / 15);
                pixels[offset + 2] = (byte)(255 * x / 15);
                pixels[offset + 3] = 255;
            }

            frame = new VideoFrame(
                TimeSpan.FromTicks(TimeSpan.TicksPerSecond * index / 30),
                Format,
                [pixels],
                [16 * 4]);
            return true;
        }
    }

    private sealed class CountingVideoOutput : IVideoOutput
    {
        private int _count;
        private int _topLeft;
        private int _bottomRight;
        public VideoFormat Format { get; private set; } = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => [];
        public int Count => Volatile.Read(ref _count);
        public (byte B, byte R) TopLeft => Unpack(Volatile.Read(ref _topLeft));
        public (byte B, byte R) BottomRight => Unpack(Volatile.Read(ref _bottomRight));
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            var pixels = frame.Planes[0].Span;
            Volatile.Write(ref _topLeft, Pack(pixels[0], pixels[2]));
            var bottomRight = (frame.Format.Height - 1) * frame.Strides[0] + (frame.Format.Width - 1) * 4;
            Volatile.Write(ref _bottomRight, Pack(pixels[bottomRight], pixels[bottomRight + 2]));
            Interlocked.Increment(ref _count);
            frame.Dispose();
        }

        private static int Pack(byte b, byte r) => b | (r << 8);

        private static (byte B, byte R) Unpack(int value) => ((byte)value, (byte)(value >> 8));
    }
}

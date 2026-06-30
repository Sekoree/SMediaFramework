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
        // lease declares DisposeOutputOnRuntimeDispose=false — the session must not dispose them, NXT-01).
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

            frame = new VideoFrame(
                TimeSpan.FromTicks(TimeSpan.TicksPerSecond * index / 30),
                Format,
                [new byte[16 * 16 * 4]],
                [16 * 4]);
            return true;
        }
    }

    private sealed class CountingVideoOutput : IVideoOutput
    {
        private int _count;
        public VideoFormat Format { get; private set; } = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => [];
        public int Count => Volatile.Read(ref _count);
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            Interlocked.Increment(ref _count);
            frame.Dispose();
        }
    }
}

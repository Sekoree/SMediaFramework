using HaPlay.ViewModels;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using System.Text;

namespace HaPlay.Playback;

/// <summary>
/// Emits delta throughput / drop counters every few seconds while playback runs (Debug log level).
/// </summary>
internal sealed class PlaybackThroughputDiagnostics
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.PlaybackThroughputDiagnostics");

    private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(5);

    private DateTime _lastLogUtc = DateTime.MinValue;
    private long _prevDecoded;
    private long _prevDisplayed;
    private long _prevDropLate;
    private long _prevDropDrain;
    private long _prevNdiUnpacked;
    private long _prevNdiUnpackDrops;
    private long _prevNdiOverflow;
    private readonly Dictionary<Guid, (long Submitted, long Dropped)> _prevVideoByLine = new();
    private readonly Dictionary<Guid, (long Enqueued, long Processed, long Dropped)> _prevAudioByLine = new();
    private readonly Dictionary<Guid, long> _prevPortAudioUnderruns = new();

    public void Reset()
    {
        _lastLogUtc = DateTime.MinValue;
        _prevDecoded = _prevDisplayed = _prevDropLate = _prevDropDrain = 0;
        _prevNdiUnpacked = _prevNdiUnpackDrops = _prevNdiOverflow = 0;
        _prevVideoByLine.Clear();
        _prevAudioByLine.Clear();
        _prevPortAudioUnderruns.Clear();
    }

    public void TryLogPeriodic(HaPlayPlaybackSession? session, IReadOnlyList<OutputLineViewModel> lines)
    {
        if (session is null || !Trace.IsEnabled(LogLevel.Debug))
            return;

        var now = DateTime.UtcNow;
        if (_lastLogUtc != DateTime.MinValue && now - _lastLogUtc < LogInterval)
            return;
        _lastLogUtc = now;

        var video = session.Player.Video;
        var decoded = video.DecodedCount;
        var displayed = video.DisplayedCount;
        var dropLate = video.DroppedLate;
        var dropDrain = video.DroppedDrain;

        var dDecoded = decoded - _prevDecoded;
        var dDisplayed = displayed - _prevDisplayed;
        var dDropLate = dropLate - _prevDropLate;
        var dDropDrain = dropDrain - _prevDropDrain;
        _prevDecoded = decoded;
        _prevDisplayed = displayed;
        _prevDropLate = dropLate;
        _prevDropDrain = dropDrain;

        var sb = new StringBuilder(256);
        sb.Append("PlaybackStats [5s]: player decode +").Append(dDecoded)
            .Append(" display +").Append(dDisplayed)
            .Append(" dropLate +").Append(dDropLate)
            .Append(" dropDrain +").Append(dDropDrain);

        if (session.TryGetNdiReceiverStats(out var ndiUnpacked, out var ndiUnpackDrops, out var ndiOverflow))
        {
            var dUnpacked = ndiUnpacked - _prevNdiUnpacked;
            var dUnpackDrops = ndiUnpackDrops - _prevNdiUnpackDrops;
            var dOverflow = ndiOverflow - _prevNdiOverflow;
            _prevNdiUnpacked = ndiUnpacked;
            _prevNdiUnpackDrops = ndiUnpackDrops;
            _prevNdiOverflow = ndiOverflow;
            sb.Append(" | ndi recv +").Append(dUnpacked)
                .Append(" unpackFail +").Append(dUnpackDrops)
                .Append(" recvOverflow +").Append(dOverflow);
        }

        foreach (var line in lines)
        {
            if (!session.HasWiredLine(line))
                continue;

            var name = line.Definition.DisplayName;
            if (session.TryGetVideoHealthMetrics(line, out var vm))
            {
                _prevVideoByLine.TryGetValue(line.Definition.Id, out var pv);
                var dSub = vm.SubmittedFrames - pv.Submitted;
                var dDrop = vm.DroppedFrames - pv.Dropped;
                _prevVideoByLine[line.Definition.Id] = (vm.SubmittedFrames, vm.DroppedFrames);
                sb.Append(" | video '").Append(name)
                    .Append("' sub +").Append(dSub)
                    .Append(" drop +").Append(dDrop)
                    .Append(" q=").Append(vm.CurrentQueuedDepth)
                    .Append('/').Append(vm.MaxQueueDepth);
            }

            if (session.TryGetAudioHealthMetrics(line, out var ast))
            {
                _prevAudioByLine.TryGetValue(line.Definition.Id, out var pa);
                var dEnq = ast.Enqueued - pa.Enqueued;
                var dProc = ast.Processed - pa.Processed;
                var dDrop = ast.Dropped - pa.Dropped;
                _prevAudioByLine[line.Definition.Id] = (ast.Enqueued, ast.Processed, ast.Dropped);
                sb.Append(" | audio '").Append(name)
                    .Append("' enq +").Append(dEnq)
                    .Append(" proc +").Append(dProc)
                    .Append(" drop +").Append(dDrop);
            }

            var paUnder = session.GetPortAudioUnderrunDelta(line);
            _prevPortAudioUnderruns.TryGetValue(line.Definition.Id, out var prevUnder);
            var dUnder = paUnder - prevUnder;
            _prevPortAudioUnderruns[line.Definition.Id] = paUnder;
            if (dUnder > 0)
                sb.Append(" | portAudio '").Append(name).Append("' underrun +").Append(dUnder);
        }

        Trace.LogDebug("{Stats}", sb.ToString());
    }
}

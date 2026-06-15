using Avalonia.Threading;
using HaPlay.Resources;
using HaPlay.ViewModels;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.NDI;
using S.Media.Playback;
using S.Media.PortAudio;

namespace HaPlay.Playback;

public sealed partial class CuePlaybackEngine
{
    private void WireAudioRoutes(ActiveCue entry, Dictionary<Guid, List<AudioRoutePlanEntry>> audioByOutput)
    {
        if (audioByOutput.Count == 0) return;

        IAudioSource decoderAudio;
        if (!TryGetCueAudioSource(entry, out decoderAudio))
            return;

        var fanout = new AudioSourceFanout(decoderAudio);
        entry.AudioFanout = fanout;
        entry.AudioDisposables.Add(fanout);

        foreach (var (lineId, routes) in audioByOutput)
            CreateActiveAudioOutput(entry, lineId, routes);
    }

    private ActiveAudioOutput? CreateActiveAudioOutput(
        ActiveCue entry,
        Guid lineId,
        IReadOnlyList<AudioRoutePlanEntry> routes)
    {
        if (routes.Count == 0 || entry.AudioFanout is null)
            return null;

        var runtime = GetOrCreateAudioRuntime(lineId);
        if (runtime is null) return null;

        var routedSource = entry.AudioFanout.CreateBranch();
        var pausable = new PausableAudioSource(routedSource, disposeInner: true)
        {
            IsPaused = entry.IsPaused,
        };
        entry.PausableAudioSources.Add(pausable);
        entry.AudioDisposables.Add(pausable);

        var sourceIdHint = BuildAudioSourceId(entry, lineId);
        var routeSpecs = routes.Select(route => ToAudioRouteSpec(route.Route)).ToArray();
        var srcId = runtime.AddSource(
            pausable,
            routeSpecs,
            sourceIdHint,
            (sourceId, routeOrdinal) => BuildAudioRouteId(sourceId, routes[routeOrdinal].SourceIndex));
        entry.AudioSources.Add((runtime, srcId));

        var output = new ActiveAudioOutput(lineId, runtime, srcId, pausable);
        entry.AudioOutputsByLine[lineId] = output;
        foreach (var route in routes)
        {
            var routeId = BuildAudioRouteId(srcId, route.SourceIndex);
            entry.AudioRoutesByIndex[route.SourceIndex] = new ActiveAudioRoute(
                lineId,
                runtime,
                srcId,
                routeId,
                route.Route);
        }

        if (runtime.PlaybackClock is { } playbackClock)
            entry.PlaybackClockMaster ??= playbackClock;
        return output;
    }

    private void ReconcileActiveAudioRoutes(ActiveCue entry, IReadOnlyList<CueAudioRoute> routes)
    {
        var desired = routes
            .Select((route, sourceIndex) => new AudioRoutePlanEntry(route, sourceIndex))
            .Where(route => route.Route.OutputLineId != Guid.Empty)
            .ToDictionary(route => route.SourceIndex);

        if (desired.Count > 0 && entry.AudioFanout is null)
        {
            if (!TryGetCueAudioSource(entry, out var decoderAudio))
                return;
            entry.AudioFanout = new AudioSourceFanout(decoderAudio);
            entry.AudioDisposables.Add(entry.AudioFanout);
        }

        foreach (var (sourceIndex, active) in entry.AudioRoutesByIndex.ToArray())
        {
            if (!desired.TryGetValue(sourceIndex, out var route)
                || route.Route.OutputLineId != active.OutputLineId)
            {
                RemoveActiveAudioRoute(entry, sourceIndex);
            }
        }

        foreach (var route in desired.Values.OrderBy(route => route.SourceIndex))
        {
            if (entry.AudioRoutesByIndex.TryGetValue(route.SourceIndex, out var active)
                && active.OutputLineId == route.Route.OutputLineId)
            {
                UpdateActiveAudioRoute(entry, route.SourceIndex, active, route.Route);
                continue;
            }

            AddActiveAudioRoute(entry, route);
        }

        ReleaseEmptyRuntimes();
    }

    private void AddActiveAudioRoute(ActiveCue entry, AudioRoutePlanEntry route)
    {
        var lineId = route.Route.OutputLineId;
        try
        {
            if (!entry.AudioOutputsByLine.TryGetValue(lineId, out var output))
            {
                CreateActiveAudioOutput(entry, lineId, [route]);
                return;
            }

            var routeId = BuildAudioRouteId(output.SourceId, route.SourceIndex);
            output.Runtime.UpdateRoute(output.SourceId, routeId, ToAudioRouteSpec(route.Route));
            entry.AudioRoutesByIndex[route.SourceIndex] = new ActiveAudioRoute(
                lineId,
                output.Runtime,
                output.SourceId,
                routeId,
                route.Route);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.AddActiveAudioRoute: route {RouteIndex}", route.SourceIndex);
        }
    }

    private void UpdateActiveAudioRoute(
        ActiveCue entry,
        int routeIndex,
        ActiveAudioRoute active,
        CueAudioRoute route)
    {
        try
        {
            if (active.Route.SourceChannel == route.SourceChannel
                && active.Route.OutputChannel == route.OutputChannel)
            {
                active.Runtime.SetRouteGain(active.SourceId, active.RouteId, route.GainDb, route.Muted);
            }
            else
            {
                active.Runtime.UpdateRoute(active.SourceId, active.RouteId, ToAudioRouteSpec(route));
            }

            entry.AudioRoutesByIndex[routeIndex] = active with { Route = route };
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.UpdateActiveAudioRoute: route {RouteIndex}", routeIndex);
        }
    }

    private void RemoveActiveAudioRoute(ActiveCue entry, int routeIndex)
    {
        if (!entry.AudioRoutesByIndex.Remove(routeIndex, out var active))
            return;

        active.Runtime.RemoveRoute(active.SourceId, active.RouteId);
        RemoveActiveAudioOutputIfEmpty(entry, active.OutputLineId);
    }

    private void RemoveActiveAudioOutputIfEmpty(ActiveCue entry, Guid lineId)
    {
        if (entry.AudioRoutesByIndex.Values.Any(route => route.OutputLineId == lineId))
            return;
        if (!entry.AudioOutputsByLine.Remove(lineId, out var output))
            return;

        output.Runtime.RemoveSource(output.SourceId);
        entry.AudioSources.RemoveAll(source =>
            ReferenceEquals(source.Runtime, output.Runtime)
            && string.Equals(source.SourceId, output.SourceId, StringComparison.Ordinal));
        entry.PausableAudioSources.Remove(output.Source);
        entry.AudioDisposables.Remove(output.Source);
        try { output.Source.Dispose(); }
        catch (Exception ex) { Trace.LogWarning(ex, "CuePlaybackEngine.RemoveActiveAudioOutputIfEmpty: source dispose"); }
    }

    private static string BuildAudioSourceId(ActiveCue entry, Guid lineId) =>
        $"cue_{entry.Cue.Id:N}_{entry.InstanceId:N}_{lineId:N}";

    private static string BuildAudioRouteId(string sourceId, int routeIndex) =>
        $"{sourceId}_ar{routeIndex}";

    private static bool TryGetCueAudioSource(ActiveCue entry, out IAudioSource audioSource)
    {
        audioSource = null!;
        if (entry.ArmedClip.Spec.Source is HaPlayLiveClipMediaSource live)
        {
            if (live.AudioSource is not { } liveAudio)
                return false;
            audioSource = liveAudio;
            return true;
        }

        try
        {
            if (!entry.Player.HasContainerDecoder || !entry.Player.Decoder.HasAudio)
                return false;
            audioSource = entry.Player.Decoder.Audio;
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CuePlaybackEngine.WireAudioRoutes: source has no audio");
            return false;
        }
    }
}

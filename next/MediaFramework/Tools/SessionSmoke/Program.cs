// Phase 4 SessionSmoke — a full show runs headless (the Phase-4 exit gate). Builds a ShowDocument, round-
// trips it through JSON (D10 persistence), loads it into a ShowSession, then drives the dispatcher:
//   GO → cue 1 (audio) plays on the master output; seek jumps the playhead;
//   GO → cue 2 (video) plays onto a composition canvas → the CPU compositor composites it headless.
// No Avalonia, no GL — proves the cue → clip → audio-master and cue → clip → video-layer → composite paths.
using S.Media.Audio.PortAudio;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Interop;
using S.Media.Session;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: SessionSmoke <audio-file> [video-file]");
    return 1;
}

var audioFile = args[0];
var videoFile = args.Length > 1 ? args[1] : args[0];

// A sidecar SRT shown over the video cue's composition — a non-ASS format, so it exercises the full
// FFmpeg-decode → ASS events → libass path through the unified factory, end-to-end.
var subPath = Path.Combine(Path.GetTempPath(), "sessionsmoke-subs.srt");
File.WriteAllText(subPath, "1\n00:00:00,000 --> 02:46:39,000\nSessionSmoke subtitle layer\n");

// Verify the unified host factory (FFmpeg decode + libass render) independently: load the SRT, render a frame.
using (var subProbe = SubtitleOverlayFactory.FromFile(subPath, 1280, 720))
{
    var subFrame = subProbe?.RenderAt(TimeSpan.FromSeconds(1));
    Console.WriteLine($"subtitle factory: source={subProbe is not null}, renders={subFrame is not null} " +
                      $"({subFrame?.Format.Width}x{subFrame?.Format.Height})");
    if (subFrame is null)
    {
        Console.Error.WriteLine("FAIL: subtitle factory/renderer produced no overlay");
        return 8;
    }
}

var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()).Use(new PortAudioModule()));
var backend = registry.AudioBackends.FirstOrDefault();
if (backend is null)
{
    Console.Error.WriteLine("no audio backend registered");
    return 3;
}

// Author a two-cue show — an audio cue and a video cue on a composition — then prove D10: serialize → JSON
// → deserialize and drive the reloaded copy.
var document = new ShowDocument(
    Version: 1,
    Cues:
    [
        new CueDefinition("cue1", 1, "Audio Clip"),
        new CueDefinition("cue2", 2, "Video Clip"),
    ],
    Clips:
    [
        new ShowClipBinding("cue1", audioFile),
        new ShowClipBinding("cue2", videoFile, CompositionId: "screen", LayerIndex: 0, SubtitlePath: subPath),
    ],
    Compositions:
    [
        // The composition carries an affine output mapping (projector tiling / keystone) — the composited
        // canvas is cut into placed sections drawn onto the output. Affine sections composite headless on
        // the CPU backend (mesh warp is GL-only); here a single full-canvas section exercises the path.
        new ShowComposition("screen", "Main Screen", 1280, 720, 24, 1,
            OutputMapping: new ClipOutputMappingSpec(
                Sections:
                [
                    new ClipOutputMappingSection("full", Enabled: true,
                        SrcX: 0, SrcY: 0, SrcWidth: 1, SrcHeight: 1,
                        DestX: 0, DestY: 0, DestWidth: 1280, DestHeight: 720),
                ],
                OutputWidth: 1280, OutputHeight: 720)),
    ],
    Outputs: [],
    Routes: [],
    Devices: []);

var json = document.ToJson();
var reloaded = ShowDocument.FromJson(json);
Console.WriteLine($"decoders: {string.Join(", ", registry.Decoders.Select(d => d.Name))}; backend: {backend.Name}");
Console.WriteLine($"show: {reloaded.Cues.Count} cues, {reloaded.Clips.Count} clips, {reloaded.Compositions.Count} compositions (JSON {json.Length} B round-tripped)");

await using var session = new ShowSession(
    registry,
    backend,
    (path, streamIndex, width, height) => SubtitleOverlayFactory.FromFileDeferred(path, width, height, streamIndex));
session.LoadDocument(reloaded);

// GO → cue 1 fires (audio clip opens through the registry + plays on the master output).
var go1 = await session.GoAsync();
await Task.Delay(1000);
var afterFire = (await session.SnapshotAsync())[0];

// Seek the active clip forward; the playhead must jump.
await session.SeekAsync(TimeSpan.FromSeconds(5));
await Task.Delay(500);
var afterSeek = (await session.SnapshotAsync())[0];

// GO → cue 2 fires (video clip; its video is routed onto the "screen" composition and composited headless).
var go2 = await session.GoAsync();
await Task.Delay(1500);
var comp = await session.GetCompositionStatsAsync("screen");

var log = await session.GetCueExecutionLogAsync();
Console.WriteLine($"GO1={go1} pos={afterFire.ClipPosition.TotalSeconds:F2}s running={afterFire.IsRunning}");
Console.WriteLine($"SEEK pos={afterSeek.ClipPosition.TotalSeconds:F2}s");
Console.WriteLine($"GO2={go2} composition: submitted={comp?.FramesSubmitted} composited={comp?.FramesComposited} layers={comp?.LayerCount}");
Console.WriteLine($"cue log: {string.Join(", ", log.Select(e => $"{e.Number}:{e.Status}"))}");

// --- gate assertions ---------------------------------------------------------------------------------
if (go1 != CueExecutionStatus.Fired)
{
    Console.Error.WriteLine($"FAIL: GO1 did not fire (was {go1})");
    return 2;
}

if (afterFire.ClipPosition < TimeSpan.FromMilliseconds(200) || !afterFire.IsRunning)
{
    Console.Error.WriteLine("FAIL: audio clip did not play (position did not advance under real-time pacing)");
    return 3;
}

if (afterSeek.ClipPosition < afterFire.ClipPosition + TimeSpan.FromSeconds(3))
{
    Console.Error.WriteLine($"FAIL: seek did not jump the playhead ({afterFire.ClipPosition.TotalSeconds:F2}s → {afterSeek.ClipPosition.TotalSeconds:F2}s)");
    return 4;
}

if (go2 != CueExecutionStatus.Fired)
{
    Console.Error.WriteLine($"FAIL: GO2 did not fire (was {go2})");
    return 5;
}

if (comp is not { FramesSubmitted: > 0, FramesComposited: > 0 })
{
    Console.Error.WriteLine($"FAIL: video did not composite (submitted={comp?.FramesSubmitted}, composited={comp?.FramesComposited}) — pass a video file as arg 2");
    return 6;
}

// The composition must carry TWO layers — the clip's video + the auto-attached subtitle — and still composite.
if (comp.Value.LayerCount < 2)
{
    Console.Error.WriteLine($"FAIL: subtitle layer not attached (LayerCount={comp.Value.LayerCount}, expected 2: video + subtitle)");
    return 9;
}

if (log.Count != 2 || log.Any(e => e.Status != CueExecutionStatus.Fired))
{
    Console.Error.WriteLine("FAIL: execution log did not record two fired cues");
    return 7;
}

// --- 8b trim-in: a clip with a StartOffset starts at the trim point (forward play) -----------------
await using (var trimSession = new ShowSession(registry, backend))
{
    var trimOffset = TimeSpan.FromSeconds(3);
    trimSession.LoadDocument(new ShowDocument(
        1,
        [new CueDefinition("trim", 1, "Trim-in")],
        [new ShowClipBinding("trim", audioFile) { StartOffset = trimOffset }],
        [], [], [], []));
    await trimSession.GoAsync();
    await Task.Delay(800);
    var trim = (await trimSession.SnapshotAsync())[0];
    Console.WriteLine($"TRIM-IN pos={trim.ClipPosition.TotalSeconds:F2}s dur={trim.ClipDuration.TotalSeconds:F2}s running={trim.IsRunning} (offset {trimOffset.TotalSeconds:F0}s)");

    if (!trim.IsRunning)
    {
        Console.Error.WriteLine("FAIL: trimmed clip did not play");
        return 10;
    }

    // Only assert the offset landed when the clip is long enough to honour it (a short CI tone can't).
    if (trim.ClipDuration > trimOffset + TimeSpan.FromSeconds(1) && trim.ClipPosition < trimOffset)
    {
        Console.Error.WriteLine($"FAIL: trim-in did not seek to StartOffset (pos={trim.ClipPosition.TotalSeconds:F2}s < {trimOffset.TotalSeconds:F0}s)");
        return 11;
    }
}

Console.WriteLine("SessionSmoke OK — a full show ran headless (audio cue + seek + video cue composited with a subtitle layer + trim-in).");
return 0;

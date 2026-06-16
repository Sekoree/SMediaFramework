namespace HaPlay.Playback;

public sealed partial class CuePlaybackEngine
{
    // Engine-wide audio genlock (Doc/HaPlay-MultiOutput-Sync.md, Option B). Null unless the opt-in
    // HAPLAY_MULTIOUTPUT_GENLOCK toggle is set, so when disabled the audio path is byte-identical to before:
    // GetOrCreateAudioRuntime passes no ratePpmProvider and ClipAudioOutputRuntime stays unwrapped.
    // Threaded through GetOrCreateAudioRuntime (register members) and ReleaseEmptyRuntimes (unregister);
    // disposed in Dispose. See EngineAudioGenlock for the controller.
    private readonly EngineAudioGenlock? _genlock = EngineAudioGenlock.IsEnabled ? new EngineAudioGenlock() : null;
}

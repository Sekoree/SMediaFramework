namespace S.Media.Playback;

/// <summary>One-shot prefill + hardware start callbacks for coordinated <see cref="MediaPlayer.Play"/>.</summary>
internal sealed class PlaybackAudioStartup
{
    private readonly Action _prefill;
    private readonly Action _startHardware;
    private bool _started;

    public PlaybackAudioStartup(Action prefill, Action startHardware)
    {
        _prefill = prefill ?? throw new ArgumentNullException(nameof(prefill));
        _startHardware = startHardware ?? throw new ArgumentNullException(nameof(startHardware));
    }

    public void GetCallbacks(out Action? prefill, out Action? startHardware)
    {
        if (_started)
        {
            prefill = null;
            startHardware = null;
            return;
        }

        prefill = _prefill;
        startHardware = () =>
        {
            _startHardware();
            _started = true;
        };
    }
}

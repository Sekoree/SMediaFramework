using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.OutputPreview;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>
/// Add/Edit dialog for the live-stream output: shared encode settings (same shape as the file dialog)
/// plus push targets and the LAN server. Selecting any RTMP target constrains the audio side to one
/// AAC track (FLV limit) - the extra track rows grey out with a hint instead of failing at go-live.
/// <see cref="TryCommit"/> runs the real cross-destination validation from the stream module.
/// </summary>
public partial class AddLiveStreamOutputDialogViewModel : ViewModelBase
{
    private Guid? _existingId;
    private IReadOnlyCollection<string> _existingOutputNames = Array.Empty<string>();

    public string[] OutputModes { get; } = ["VideoAndAudio", "VideoOnly", "AudioOnly"];
    public string[] VideoCodecs { get; } = ["H264", "Hevc"];
    public string[] AudioCodecs { get; } = ["Aac", "Opus"];
    public string[] Presets { get; } = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium"];

    public AddLiveStreamOutputDialogViewModel()
    {
        PushTargets.CollectionChanged += OnPushTargetsChanged;
    }

    [ObservableProperty] private string _displayName = Strings.OutputKindLiveStreamLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoSettings))]
    [NotifyPropertyChangedFor(nameof(ShowAudioSettings))]
    private string _outputMode = "VideoAndAudio";

    public bool ShowVideoSettings => OutputMode != "AudioOnly";
    public bool ShowAudioSettings => OutputMode != "VideoOnly";

    [ObservableProperty] private string _videoCodec = "H264";
    [ObservableProperty] private int _videoBitrateKbps = 6000;
    [ObservableProperty] private string _videoPreset = "veryfast";
    [ObservableProperty] private int _scaleWidth;
    [ObservableProperty] private int _scaleHeight;

    public ObservableCollection<AudioLegRowViewModel> AudioLegs { get; } = [new AudioLegRowViewModel()];

    public ObservableCollection<StreamPushTargetRowViewModel> PushTargets { get; } = [];

    [ObservableProperty] private bool _localServerEnabled = true;
    [ObservableProperty] private int _localServerPort = 8620;
    [ObservableProperty] private bool _localServerTs = true;
    [ObservableProperty] private bool _localServerHls = true;

    [ObservableProperty] private string? _validationMessage;

    /// <summary>True when any RTMP push target exists → FLV limits (one AAC track, H.264) apply.</summary>
    public bool HasRtmpTarget => PushTargets.Any(t => t.Protocol == "Rtmp");

    public bool IsEditing => _existingId is not null;
    public string DialogTitle => IsEditing ? Strings.EditLiveStreamDialogTitle : Strings.AddLiveStreamDialogTitle;
    public string PrimaryButtonLabel => IsEditing ? Strings.SaveButton : Strings.AddButton;

    private void OnPushTargetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (StreamPushTargetRowViewModel row in e.NewItems)
                row.PropertyChanged += (_, pe) =>
                {
                    if (pe.PropertyName == nameof(StreamPushTargetRowViewModel.Protocol))
                        OnPropertyChanged(nameof(HasRtmpTarget));
                };
        OnPropertyChanged(nameof(HasRtmpTarget));
    }

    [RelayCommand]
    private void AddPushTarget() => PushTargets.Add(new StreamPushTargetRowViewModel());

    [RelayCommand]
    private void RemovePushTarget(StreamPushTargetRowViewModel row) => PushTargets.Remove(row);

    [RelayCommand]
    private void AddAudioLeg() => AudioLegs.Add(new AudioLegRowViewModel { Codec = "Aac" });

    [RelayCommand]
    private void RemoveAudioLeg(AudioLegRowViewModel row)
    {
        if (AudioLegs.Count > 1)
            AudioLegs.Remove(row);
    }

    public void InitializeExistingOutputNames(IEnumerable<string> names)
    {
        var set = OutputNameUniqueness.CreateNameSet(names);
        _existingOutputNames = set;
        if (!IsEditing)
            DisplayName = OutputNameUniqueness.MakeUniqueDefaultName(DisplayName, set);
    }

    public void LoadFromExisting(LiveStreamOutputDefinition existing)
    {
        _existingId = existing.Id;
        DisplayName = existing.DisplayName;

        var encode = existing.EffectiveEncode;
        OutputMode = encode.OutputMode;
        VideoCodec = encode.VideoCodec;
        VideoBitrateKbps = encode.VideoBitrateBps > 0 ? (int)(encode.VideoBitrateBps / 1000) : 6000;
        VideoPreset = encode.VideoPreset ?? "veryfast";
        ScaleWidth = encode.ScaleWidth;
        ScaleHeight = encode.ScaleHeight;

        AudioLegs.Clear();
        foreach (var leg in encode.AudioLegs)
            AudioLegs.Add(AudioLegRowViewModel.FromDefinition(leg));
        if (AudioLegs.Count == 0)
            AudioLegs.Add(new AudioLegRowViewModel());

        PushTargets.Clear();
        foreach (var target in existing.PushTargets)
            PushTargets.Add(new StreamPushTargetRowViewModel { Protocol = target.Protocol, Url = target.Url });

        var server = existing.EffectiveLocalServer;
        LocalServerEnabled = server.Enabled;
        LocalServerPort = server.Port;
        LocalServerTs = server.EnableTs;
        LocalServerHls = server.EnableHls;

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
        OnPropertyChanged(nameof(HasRtmpTarget));
    }

    public LiveStreamOutputDefinition? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = Strings.ValidationDisplayNameRequired;
            return null;
        }

        var displayName = DisplayName.Trim();
        if (OutputNameUniqueness.TryFindDuplicate(displayName, _existingOutputNames, out var duplicateName))
        {
            ValidationMessage = Strings.Format(nameof(Strings.ValidationOutputNameAlreadyExistsFormat), duplicateName);
            return null;
        }

        var definition = new LiveStreamOutputDefinition(
            _existingId ?? Guid.NewGuid(),
            displayName,
            new EncodeSettingsDefinition(
                Container: "MpegTs",
                OutputMode,
                VideoCodec,
                VideoBitrateKbps * 1000L, // live streams are bitrate-driven (CBR-ish for network pacing)
                VideoCrf: null,
                VideoPreset,
                GopSize: 60, // ~2 s at 30 fps - keyframe cadence for joiners/segments
                ScaleWidth,
                ScaleHeight)
            {
                AudioLegs = AudioLegs.Select(l => l.ToDefinition()).ToArray(),
            },
            new LocalStreamServerDefinition(LocalServerEnabled, LocalServerPort, LocalServerTs, LocalServerHls))
        {
            PushTargets = PushTargets
                .Where(t => !string.IsNullOrWhiteSpace(t.Url))
                .Select(t => new StreamPushTargetDefinition(t.Protocol, t.Url.Trim()))
                .ToArray(),
        };

        var errors = LiveStreamOutputRuntime.BuildOptions(definition).Validate();
        if (errors.Count > 0)
        {
            ValidationMessage = $"{Strings.FileOutputValidationHeader} {string.Join(" ", errors)}";
            return null;
        }

        return definition;
    }
}

/// <summary>One editable push destination row.</summary>
public partial class StreamPushTargetRowViewModel : ViewModelBase
{
    public string[] Protocols { get; } = ["Rtmp", "Srt", "Rtsp"];

    [ObservableProperty] private string _protocol = "Rtmp";
    [ObservableProperty] private string _url = "";
}

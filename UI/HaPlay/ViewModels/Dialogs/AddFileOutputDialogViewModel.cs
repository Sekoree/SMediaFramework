using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.OutputPreview;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>
/// Add/Edit dialog for the record-to-file output. Container→codec compatibility follows the encode
/// module's option validation - <see cref="TryCommit"/> runs the real
/// <c>EncodeSessionOptions.Validate()</c> (including encoder-availability probes) so the operator
/// learns about a missing libx264 here, not when they hit Record.
/// </summary>
public partial class AddFileOutputDialogViewModel : ViewModelBase
{
    private Guid? _existingId;
    private IReadOnlyCollection<string> _existingOutputNames = Array.Empty<string>();

    public EncodeChoice[] Containers { get; } = EncodeChoices.Containers;
    public EncodeChoice[] OutputModes { get; } = EncodeChoices.OutputModes;
    public EncodeChoice[] VideoCodecs { get; } = EncodeChoices.VideoCodecs;
    public EncodeChoice[] AudioCodecs { get; } = EncodeChoices.AudioCodecs;
    public string[] Presets { get; } = EncodeChoices.Presets;
    public EncodeChoice[] RecordingModes { get; } =
    [
        new(Strings.FileOutputRecordingContinuousChoice, FileOutputDefinition.ContinuousProgramRecordingMode),
        new(Strings.FileOutputRecordingContentOnlyChoice, FileOutputDefinition.ContentOnlyRecordingMode),
    ];

    [ObservableProperty] private string _displayName = Strings.OutputKindFileRecordLabel;
    [ObservableProperty] private string _directoryPath =
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } v
            ? v
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] private string _fileNamePattern = "recording_{timestamp}";
    [ObservableProperty] private string _recordingMode = FileOutputDefinition.ContinuousProgramRecordingMode;
    [ObservableProperty] private string _container = "Mp4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideoSettings))]
    [NotifyPropertyChangedFor(nameof(ShowAudioSettings))]
    private string _outputMode = "VideoAndAudio";

    public bool ShowVideoSettings => OutputMode != "AudioOnly";
    public bool ShowAudioSettings => OutputMode != "VideoOnly";

    [ObservableProperty] private string _videoCodec = "H264";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseCrf))]
    private bool _useBitrate; // false = CRF quality mode (default)

    public bool UseCrf => !UseBitrate;

    [ObservableProperty] private int _videoCrf = 23;
    [ObservableProperty] private int _videoBitrateKbps = 8000;
    [ObservableProperty] private string _videoPreset = "veryfast";
    [ObservableProperty] private int _scaleWidth = 1920;
    [ObservableProperty] private int _scaleHeight = 1080;
    // A continuous program needs a known shape before the first cue so Arm can record black from t=0.
    // New outputs mirror the stream dialog's valid-out-of-box 1080p30 carrier defaults.
    [ObservableProperty] private int _fps = 30;

    public ObservableCollection<AudioLegRowViewModel> AudioLegs { get; } = [new AudioLegRowViewModel()];

    [ObservableProperty] private string? _validationMessage;

    public bool IsEditing => _existingId is not null;
    public string DialogTitle => IsEditing ? Strings.EditFileOutputDialogTitle : Strings.AddFileOutputDialogTitle;
    public string PrimaryButtonLabel => IsEditing ? Strings.SaveButton : Strings.AddButton;

    [RelayCommand]
    private void AddAudioLeg() => AudioLegs.Add(new AudioLegRowViewModel());

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

    public void LoadFromExisting(FileOutputDefinition existing)
    {
        _existingId = existing.Id;
        DisplayName = existing.DisplayName;
        DirectoryPath = existing.DirectoryPath;
        FileNamePattern = existing.FileNamePattern;
        RecordingMode = existing.EffectiveRecordingMode;

        var encode = existing.EffectiveEncode;
        Container = encode.Container;
        OutputMode = encode.OutputMode;
        VideoCodec = encode.VideoCodec;
        UseBitrate = encode.VideoBitrateBps > 0;
        VideoBitrateKbps = encode.VideoBitrateBps > 0 ? (int)(encode.VideoBitrateBps / 1000) : 8000;
        VideoCrf = encode.VideoCrf ?? 23;
        VideoPreset = encode.VideoPreset ?? "veryfast";
        ScaleWidth = encode.ScaleWidth;
        ScaleHeight = encode.ScaleHeight;
        Fps = encode.Fps;

        AudioLegs.Clear();
        foreach (var leg in encode.AudioLegs)
            AudioLegs.Add(AudioLegRowViewModel.FromDefinition(leg));
        if (AudioLegs.Count == 0)
            AudioLegs.Add(new AudioLegRowViewModel());

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
    }

    public FileOutputDefinition? TryCommit()
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

        if (string.IsNullOrWhiteSpace(DirectoryPath))
        {
            ValidationMessage = Strings.FileOutputDirectoryLabel;
            return null;
        }

        if (RecordingMode == FileOutputDefinition.ContinuousProgramRecordingMode
            && ShowVideoSettings
            && (ScaleWidth <= 0 || ScaleHeight <= 0 || Fps <= 0))
        {
            ValidationMessage = Strings.FileOutputContinuousFormatRequired;
            return null;
        }

        var definition = new FileOutputDefinition(
            _existingId ?? Guid.NewGuid(),
            displayName,
            DirectoryPath.Trim(),
            string.IsNullOrWhiteSpace(FileNamePattern) ? "recording_{timestamp}" : FileNamePattern.Trim(),
            new EncodeSettingsDefinition(
                Container,
                OutputMode,
                VideoCodec,
                UseBitrate ? VideoBitrateKbps * 1000L : 0,
                UseBitrate ? null : VideoCrf,
                VideoPreset,
                GopSize: 0,
                ScaleWidth,
                ScaleHeight,
                Fps)
            {
                AudioLegs = AudioLegs.Select(l => l.ToDefinition()).ToArray(),
            })
        {
            RecordingMode = RecordingMode,
        };

        // Full validation via the encode module (container/codec compatibility + encoder availability).
        var errors = FileOutputRuntime.BuildOptions(definition.EffectiveEncode).Validate();
        if (errors.Count > 0)
        {
            ValidationMessage = $"{Strings.FileOutputValidationHeader} {string.Join(" ", errors)}";
            return null;
        }

        return definition;
    }
}

/// <summary>One editable audio track row of the file/stream output dialogs.</summary>
public partial class AudioLegRowViewModel : ViewModelBase
{
    public EncodeChoice[] AudioCodecs { get; } = EncodeChoices.AudioCodecs;

    [ObservableProperty] private string _codec = "Aac";
    [ObservableProperty] private int _channels = 2;
    [ObservableProperty] private int _bitrateKbps = 192;
    [ObservableProperty] private string? _name;
    [ObservableProperty] private string? _language;

    public static AudioLegRowViewModel FromDefinition(EncodeAudioLegDefinition leg) => new()
    {
        Codec = leg.Codec,
        Channels = leg.Channels > 0 ? leg.Channels : 2,
        BitrateKbps = leg.BitrateBps > 0 ? (int)(leg.BitrateBps / 1000) : 192,
        Name = leg.Name,
        Language = leg.Language,
    };

    public EncodeAudioLegDefinition ToDefinition() => new(
        Codec,
        Codec is "Flac" or "Pcm16" ? 0 : BitrateKbps * 1000L, // lossless codecs ignore bitrate
        Channels,
        SampleRate: 0,
        string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        string.IsNullOrWhiteSpace(Language) ? null : Language.Trim());
}

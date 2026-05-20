using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using S.Media.Core.Video;

namespace HaPlay.ViewModels.Dialogs;

public partial class AddNDIOutputDialogViewModel : ViewModelBase
{
    private Guid? _existingId;
    private PixelFormat? _existingPixelFormatLock;
    private int? _existingResolutionLockWidth;
    private int? _existingResolutionLockHeight;

    public NDIOutputStreamMode[] StreamModes { get; } = Enum.GetValues<NDIOutputStreamMode>();

    [ObservableProperty] private string _displayName = "NDI program";
    [ObservableProperty] private string _sourceName = "HaPlay Output";
    [ObservableProperty] private string? _groups;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAudioSettings))]
    private NDIOutputStreamMode _streamMode = NDIOutputStreamMode.VideoAndAudio;

    public bool ShowAudioSettings => StreamMode != NDIOutputStreamMode.VideoOnly;
    [ObservableProperty] private int _audioChannelCount = 2;
    [ObservableProperty] private int _audioSampleRate = 48_000;
    [ObservableProperty] private string? _validationMessage;

    public bool IsEditing => _existingId is not null;
    public string DialogTitle => IsEditing ? "Edit NDI output" : "Add NDI output";
    public string PrimaryButtonLabel => IsEditing ? "Save" : "Add";

    public void LoadFromExisting(NDIOutputDefinition existing)
    {
        _existingId = existing.Id;
        _existingPixelFormatLock = existing.PixelFormatLock;
        _existingResolutionLockWidth = existing.ResolutionLockWidth;
        _existingResolutionLockHeight = existing.ResolutionLockHeight;

        DisplayName = existing.DisplayName;
        SourceName = existing.SourceName;
        Groups = existing.Groups;
        StreamMode = existing.StreamMode;
        AudioChannelCount = existing.AudioChannelCount == 0 ? 2 : existing.AudioChannelCount;
        AudioSampleRate = existing.AudioSampleRate == 0 ? 48_000 : existing.AudioSampleRate;

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
    }

    public NDIOutputDefinition? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = "Display name is required.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(SourceName))
        {
            ValidationMessage = "NDI source name is required.";
            return null;
        }

        if (StreamMode != NDIOutputStreamMode.VideoOnly)
        {
            if (AudioChannelCount < 1 || AudioChannelCount > 64)
            {
                ValidationMessage = "NDI audio channel count must be between 1 and 64.";
                return null;
            }

            if (AudioSampleRate is < 8000 or > 192_000)
            {
                ValidationMessage = "Audio sample rate looks invalid.";
                return null;
            }
        }

        return new NDIOutputDefinition(
            _existingId ?? Guid.NewGuid(),
            DisplayName.Trim(),
            SourceName.Trim(),
            string.IsNullOrWhiteSpace(Groups) ? null : Groups.Trim(),
            StreamMode,
            StreamMode == NDIOutputStreamMode.VideoOnly ? 0 : AudioChannelCount,
            StreamMode == NDIOutputStreamMode.VideoOnly ? 48_000 : AudioSampleRate,
            // Preserve forward-compat lock fields across edit — Phase B doesn't yet expose UI for them.
            PixelFormatLock: _existingPixelFormatLock,
            ResolutionLockWidth: _existingResolutionLockWidth,
            ResolutionLockHeight: _existingResolutionLockHeight);
    }
}

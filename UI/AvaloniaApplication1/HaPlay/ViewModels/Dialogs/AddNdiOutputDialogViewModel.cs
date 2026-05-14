using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels.Dialogs;

public partial class AddNdiOutputDialogViewModel : ViewModelBase
{
    public NdiOutputStreamMode[] StreamModes { get; } = Enum.GetValues<NdiOutputStreamMode>();

    [ObservableProperty] private string _displayName = "NDI program";
    [ObservableProperty] private string _sourceName = "HaPlay Output";
    [ObservableProperty] private string? _groups;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAudioSettings))]
    private NdiOutputStreamMode _streamMode = NdiOutputStreamMode.VideoAndAudio;

    public bool ShowAudioSettings => StreamMode != NdiOutputStreamMode.VideoOnly;
    [ObservableProperty] private int _audioChannelCount = 2;
    [ObservableProperty] private int _audioSampleRate = 48_000;
    [ObservableProperty] private string? _validationMessage;

    public NdiOutputDefinition? TryCommit()
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

        if (StreamMode != NdiOutputStreamMode.VideoOnly)
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

        return new NdiOutputDefinition(
            Guid.NewGuid(),
            DisplayName.Trim(),
            SourceName.Trim(),
            string.IsNullOrWhiteSpace(Groups) ? null : Groups.Trim(),
            StreamMode,
            StreamMode == NdiOutputStreamMode.VideoOnly ? 0 : AudioChannelCount,
            StreamMode == NdiOutputStreamMode.VideoOnly ? 48_000 : AudioSampleRate);
    }
}

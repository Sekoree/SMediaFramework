using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;
using S.Media.Core.Video;

namespace HaPlay.ViewModels.Dialogs;

public partial class AddNDIOutputDialogViewModel : ViewModelBase
{
    private Guid? _existingId;

    public NDIOutputStreamMode[] StreamModes { get; } = Enum.GetValues<NDIOutputStreamMode>();

    /// <summary>Phase C polish (§4.3.5) — pixel-format lock choices presented in the dialog. "Auto"
    /// (the <see cref="NDIPixelFormatChoice.Auto"/> entry) means "no lock — let the negotiator pick";
    /// every other entry is one of <see cref="NDIVideoSender.AcceptedFormats"/> (UYVY / BGRA32 / RGBA32
    /// / NV12 / I420). Other framework <see cref="PixelFormat"/> values aren't reachable for NDI senders
    /// so the dialog leaves them out.</summary>
    public NDIPixelFormatChoice[] PixelFormatChoices { get; } = NDIPixelFormatChoice.All;

    /// <summary>Phase C polish — resolution-lock presets. "Auto" means no lock; the rest are fixed
    /// (W, H) pairs receivers will see regardless of source dimensions.</summary>
    public NDIResolutionChoice[] ResolutionChoices { get; } = NDIResolutionChoice.All;

    [ObservableProperty] private string _displayName = Strings.NdiProgramDefaultName;
    [ObservableProperty] private string _sourceName = Strings.NdiOutputDefaultSourceName;
    [ObservableProperty] private string? _groups;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAudioSettings))]
    private NDIOutputStreamMode _streamMode = NDIOutputStreamMode.VideoAndAudio;

    public bool ShowAudioSettings => StreamMode != NDIOutputStreamMode.VideoOnly;
    [ObservableProperty] private int _audioChannelCount = 2;
    [ObservableProperty] private int _audioSampleRate = 48_000;

    /// <summary>Selected pixel-format lock. The <c>Auto</c> entry maps back to a null lock on
    /// <see cref="TryCommit"/>; the other entries map to their <see cref="PixelFormat"/>.</summary>
    [ObservableProperty] private NDIPixelFormatChoice _selectedPixelFormat = NDIPixelFormatChoice.Auto;

    [ObservableProperty] private NDIResolutionChoice _selectedResolution = NDIResolutionChoice.Auto;

    [ObservableProperty] private string? _validationMessage;

    public bool IsEditing => _existingId is not null;
    public string DialogTitle => IsEditing ? Strings.EditNdiOutputDialogTitle : Strings.AddNdiOutputDialogTitle;
    public string PrimaryButtonLabel => IsEditing ? Strings.SaveButton : Strings.AddButton;

    public void LoadFromExisting(NDIOutputDefinition existing)
    {
        _existingId = existing.Id;

        DisplayName = existing.DisplayName;
        SourceName = existing.SourceName;
        Groups = existing.Groups;
        StreamMode = existing.StreamMode;
        AudioChannelCount = existing.AudioChannelCount == 0 ? 2 : existing.AudioChannelCount;
        AudioSampleRate = existing.AudioSampleRate == 0 ? 48_000 : existing.AudioSampleRate;

        SelectedPixelFormat = existing.PixelFormatLock is { } pf
            ? NDIPixelFormatChoice.FromPixelFormat(pf) ?? NDIPixelFormatChoice.Auto
            : NDIPixelFormatChoice.Auto;
        SelectedResolution = NDIResolutionChoice.FromLock(existing.ResolutionLockWidth, existing.ResolutionLockHeight);

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
    }

    public NDIOutputDefinition? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = Strings.ValidationDisplayNameRequired;
            return null;
        }

        if (string.IsNullOrWhiteSpace(SourceName))
        {
            ValidationMessage = Strings.ValidationNdiSourceNameRequired;
            return null;
        }

        if (StreamMode != NDIOutputStreamMode.VideoOnly)
        {
            if (AudioChannelCount < 1 || AudioChannelCount > 64)
            {
                ValidationMessage = Strings.ValidationNdiAudioChannelCountInvalid;
                return null;
            }

            if (AudioSampleRate is < 8000 or > 192_000)
            {
                ValidationMessage = Strings.ValidationAudioSampleRateInvalid;
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
            PixelFormatLock: SelectedPixelFormat.PixelFormat,
            ResolutionLockWidth: SelectedResolution.Width,
            ResolutionLockHeight: SelectedResolution.Height);
    }
}

/// <summary>Combo option for <see cref="AddNDIOutputDialogViewModel.PixelFormatChoices"/>. Wraps a
/// nullable <see cref="PixelFormat"/> so the "Auto" (no lock) row sits at the top of the list as a
/// first-class entry rather than a magic null.</summary>
public sealed record NDIPixelFormatChoice(string Label, PixelFormat? PixelFormat)
{
    public override string ToString() => Label;

    public static readonly NDIPixelFormatChoice Auto = new(Strings.NdiPixelFormatAutoLabel, null);

    public static readonly NDIPixelFormatChoice[] All =
    [
        Auto,
        new(Strings.NdiPixelFormatUyvyLabel, S.Media.Core.Video.PixelFormat.Uyvy),
        new(Strings.NdiPixelFormatBgraLabel, S.Media.Core.Video.PixelFormat.Bgra32),
        new(Strings.NdiPixelFormatRgbaLabel, S.Media.Core.Video.PixelFormat.Rgba32),
        new(Strings.NdiPixelFormatNv12Label, S.Media.Core.Video.PixelFormat.Nv12),
        new(Strings.NdiPixelFormatI420Label, S.Media.Core.Video.PixelFormat.I420),
    ];

    public static NDIPixelFormatChoice? FromPixelFormat(PixelFormat pf)
    {
        foreach (var c in All)
            if (c.PixelFormat == pf)
                return c;
        return null;
    }
}

/// <summary>Combo option for <see cref="AddNDIOutputDialogViewModel.ResolutionChoices"/>. Mirrors
/// the project-side <c>ResolutionLockWidth</c>/<c>ResolutionLockHeight</c> nullable pair as a single
/// well-typed choice row.</summary>
public sealed record NDIResolutionChoice(string Label, int? Width, int? Height)
{
    public override string ToString() => Label;

    public static readonly NDIResolutionChoice Auto = new(Strings.NdiResolutionAutoLabel, null, null);

    public static readonly NDIResolutionChoice[] All =
    [
        Auto,
        new(Strings.NdiResolution1080Label, 1920, 1080),
        new(Strings.NdiResolution720Label, 1280, 720),
        new(Strings.NdiResolution4kLabel, 3840, 2160),
        new(Strings.NdiResolution576Label, 1024, 576),
    ];

    public static NDIResolutionChoice FromLock(int? width, int? height)
    {
        if (width is null || height is null) return Auto;
        foreach (var c in All)
            if (c.Width == width && c.Height == height)
                return c;
        // Unknown saved lock — surface as a one-off label so the user sees the values without losing them.
        return new NDIResolutionChoice(
            string.Format(System.Globalization.CultureInfo.CurrentUICulture, Strings.NdiResolutionCustomLabel, width, height),
            width,
            height);
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;
using S.Media.Core.Video;

namespace HaPlay.ViewModels.Dialogs;

public partial class AddNDIOutputDialogViewModel : ViewModelBase
{
    private Guid? _existingId;
    private IReadOnlyCollection<string> _existingOutputNames = Array.Empty<string>();

    public NDIOutputStreamMode[] StreamModes { get; } = Enum.GetValues<NDIOutputStreamMode>();

    /// <summary>Phase C polish (§4.3.5) - pixel-format lock choices presented in the dialog. "Auto"
    /// (the <see cref="NDIPixelFormatChoice.Auto"/> entry) means "no lock - let the negotiator pick";
    /// every other entry is one of <see cref="NDIVideoSender.AcceptedFormats"/> (UYVY / BGRA32 / RGBA32
    /// / NV12 / I420). Other framework <see cref="PixelFormat"/> values aren't reachable for NDI senders
    /// so the dialog leaves them out.</summary>
    public NDIPixelFormatChoice[] PixelFormatChoices { get; } = NDIPixelFormatChoice.All;

    /// <summary>Phase C polish - resolution-lock presets. "Auto" means no lock; the rest are fixed
    /// (W, H) pairs receivers will see regardless of source dimensions.</summary>
    public NDIResolutionChoice[] ResolutionChoices { get; } = NDIResolutionChoice.All;

    [ObservableProperty] private string _displayName = Strings.NDIProgramDefaultName;
    [ObservableProperty] private string _sourceName = Strings.NDIOutputDefaultSourceName;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCustomResolution))]
    private NDIResolutionChoice _selectedResolution = NDIResolutionChoice.Auto;

    /// <summary>Editable resolution shown when the "Custom…" row is selected.</summary>
    [ObservableProperty] private int _customResolutionWidth = 1920;
    [ObservableProperty] private int _customResolutionHeight = 1080;

    public bool ShowCustomResolution => SelectedResolution.IsCustom;

    [ObservableProperty] private string? _validationMessage;

    public bool IsEditing => _existingId is not null;
    public string DialogTitle => IsEditing ? Strings.EditNDIOutputDialogTitle : Strings.AddNDIOutputDialogTitle;
    public string PrimaryButtonLabel => IsEditing ? Strings.SaveButton : Strings.AddButton;

    public void InitializeExistingOutputNames(IEnumerable<string> names)
    {
        var set = OutputNameUniqueness.CreateNameSet(names);
        _existingOutputNames = set;
        if (!IsEditing)
            DisplayName = OutputNameUniqueness.MakeUniqueDefaultName(DisplayName, set);
    }

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
        if (SelectedResolution.IsCustom)
        {
            CustomResolutionWidth = existing.ResolutionLockWidth!.Value;
            CustomResolutionHeight = existing.ResolutionLockHeight!.Value;
        }

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
        var displayName = DisplayName.Trim();
        if (OutputNameUniqueness.TryFindDuplicate(displayName, _existingOutputNames, out var duplicateName))
        {
            ValidationMessage = Strings.Format(nameof(Strings.ValidationOutputNameAlreadyExistsFormat), duplicateName);
            return null;
        }

        if (string.IsNullOrWhiteSpace(SourceName))
        {
            ValidationMessage = Strings.ValidationNDISourceNameRequired;
            return null;
        }

        if (StreamMode != NDIOutputStreamMode.VideoOnly)
        {
            if (AudioChannelCount < 1 || AudioChannelCount > 64)
            {
                ValidationMessage = Strings.ValidationNDIAudioChannelCountInvalid;
                return null;
            }

            if (AudioSampleRate is < 8000 or > 192_000)
            {
                ValidationMessage = Strings.ValidationAudioSampleRateInvalid;
                return null;
            }
        }

        int? resolutionLockWidth;
        int? resolutionLockHeight;
        if (SelectedResolution.IsCustom)
        {
            // NDI senders require even dimensions (the BGRA carrier packs full-width rows; planar formats
            // need even chroma), so reject odd/out-of-range custom sizes rather than failing later at Configure.
            if (CustomResolutionWidth is < 16 or > 7680 || CustomResolutionHeight is < 16 or > 4320
                || CustomResolutionWidth % 2 != 0 || CustomResolutionHeight % 2 != 0)
            {
                ValidationMessage = Strings.ValidationNDIResolutionInvalid;
                return null;
            }
            resolutionLockWidth = CustomResolutionWidth;
            resolutionLockHeight = CustomResolutionHeight;
        }
        else
        {
            resolutionLockWidth = SelectedResolution.Width;
            resolutionLockHeight = SelectedResolution.Height;
        }

        return new NDIOutputDefinition(
            _existingId ?? Guid.NewGuid(),
            displayName,
            SourceName.Trim(),
            string.IsNullOrWhiteSpace(Groups) ? null : Groups.Trim(),
            StreamMode,
            StreamMode == NDIOutputStreamMode.VideoOnly ? 0 : AudioChannelCount,
            StreamMode == NDIOutputStreamMode.VideoOnly ? 48_000 : AudioSampleRate,
            PixelFormatLock: SelectedPixelFormat.PixelFormat,
            ResolutionLockWidth: resolutionLockWidth,
            ResolutionLockHeight: resolutionLockHeight);
    }
}

/// <summary>Combo option for <see cref="AddNDIOutputDialogViewModel.PixelFormatChoices"/>. Wraps a
/// nullable <see cref="PixelFormat"/> so the "Auto" (no lock) row sits at the top of the list as a
/// first-class entry rather than a magic null.</summary>
public sealed record NDIPixelFormatChoice(string Label, PixelFormat? PixelFormat)
{
    public override string ToString() => Label;

    public static readonly NDIPixelFormatChoice Auto = new(Strings.NDIPixelFormatAutoLabel, null);

    public static readonly NDIPixelFormatChoice[] All =
    [
        Auto,
        new(Strings.NDIPixelFormatUyvyLabel, S.Media.Core.Video.PixelFormat.Uyvy),
        new(Strings.NDIPixelFormatBgraLabel, S.Media.Core.Video.PixelFormat.Bgra32),
        new(Strings.NDIPixelFormatRgbaLabel, S.Media.Core.Video.PixelFormat.Rgba32),
        new(Strings.NDIPixelFormatNv12Label, S.Media.Core.Video.PixelFormat.Nv12),
        new(Strings.NDIPixelFormatI420Label, S.Media.Core.Video.PixelFormat.I420),
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
public sealed record NDIResolutionChoice(string Label, int? Width, int? Height, bool IsCustom = false)
{
    public override string ToString() => Label;

    public static readonly NDIResolutionChoice Auto = new(Strings.NDIResolutionAutoLabel, null, null);

    /// <summary>The "Custom…" row: width/height come from the dialog's editable fields, not this record.</summary>
    public static readonly NDIResolutionChoice Custom = new(Strings.NDIResolutionCustomEntryLabel, null, null, IsCustom: true);

    public static readonly NDIResolutionChoice[] All =
    [
        Auto,
        new(Strings.NDIResolution1080Label, 1920, 1080),
        new(Strings.NDIResolution720Label, 1280, 720),
        new(Strings.NDIResolution4kLabel, 3840, 2160),
        new(Strings.NDIResolution576Label, 1024, 576),
        Custom,
    ];

    /// <summary>Maps a saved lock back to a row: a matching preset, else the "Custom…" row (the dialog
    /// populates its editable width/height from the saved values).</summary>
    public static NDIResolutionChoice FromLock(int? width, int? height)
    {
        if (width is null || height is null) return Auto;
        foreach (var c in All)
            if (!c.IsCustom && c.Width == width && c.Height == height)
                return c;
        return Custom;
    }
}

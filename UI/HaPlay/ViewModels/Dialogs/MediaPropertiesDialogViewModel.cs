using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;
using HaPlay.Resources;
using S.Media.Decode.FFmpeg;
using S.Media.Source.MMD;
using S.Media.Source.YouTube;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>One name/value row in the properties dialog (Details tab and the per-kind summaries).</summary>
public sealed record MediaPropertyRow(string Name, string Value);

/// <summary>Audio-track choice for the Tracks tab; <see cref="Index"/> null = automatic.</summary>
public sealed record MediaPropertiesAudioTrackOption(int? Index, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The common per-item properties dialog: a Details tab for every playlist item kind (probed
/// container/stream facts where a local asset exists), a Tracks tab for file items (audio track +
/// subtitle selection), a Scene tab for MMD items (summary + full scene editor + explicit physics
/// bake), and a Streams tab for YouTube items (resolved descriptors + cache state + stream editor).
/// The dialog works on a copy: every edit replaces <see cref="Current"/> (records, same
/// <see cref="PlaylistItem.Id"/>) and only OK hands the result back to the playlist.
/// </summary>
public sealed partial class MediaPropertiesDialogViewModel : ObservableObject
{
    private PlaylistItem _current;
    private bool _suppressTrackApply;

    /// <summary>Probe hooks - injectable so VM tests run without FFmpeg natives.</summary>
    internal Func<string, MediaContainerInfo> ProbeContainer { get; init; } = MediaContainerDecoder.ProbeContainer;
    internal Func<string, bool> FileExists { get; init; } = File.Exists;
    internal YouTubePreparer Preparer { get; init; } = Playback.YouTubeRuntime.Preparer;

    public MediaPropertiesDialogViewModel(PlaylistItem item)
    {
        _current = item ?? throw new ArgumentNullException(nameof(item));
        RebuildStaticState();
    }

    /// <summary>The item as edited so far; handed back to the playlist on OK.</summary>
    public PlaylistItem Current => _current;

    public string DialogTitle => Strings.Format(nameof(Strings.MediaPropertiesDialogTitleFormat), _current.DisplayName);

    public bool IsFileItem => _current is FilePlaylistItem;
    public bool IsMMDItem => _current is MMDPlaylistItem;
    public bool IsYouTubeItem => _current is YouTubePlaylistItem;

    // ---------------------------------------------------------------- Details

    public ObservableCollection<MediaPropertyRow> DetailRows { get; } = [];
    public ObservableCollection<string> StreamLines { get; } = [];

    [ObservableProperty]
    private string? _detailsStatus;

    [ObservableProperty]
    private bool _hasStreamLines;

    /// <summary>Fills the Details tab: instant static rows from the item itself, then (when the item
    /// maps to a local asset) probed container facts merged in from a worker thread.</summary>
    public async Task LoadDetailsAsync()
    {
        RebuildStaticState();

        var probePath = ProbeTargetPath();
        if (probePath is null || !FileExists(probePath))
        {
            DetailsStatus = null;
            return;
        }

        DetailsStatus = Strings.MediaPropertiesDetailsProbing;
        try
        {
            var info = await Task.Run(() => ProbeContainer(probePath)).ConfigureAwait(true);
            PublishContainerInfo(info);
            DetailsStatus = null;
        }
        catch (Exception ex)
        {
            DetailsStatus = Strings.Format(nameof(Strings.MediaPropertiesDetailsUnavailableFormat), ex.Message);
        }
    }

    /// <summary>The local asset the Details tab probes, when the item kind has one.</summary>
    private string? ProbeTargetPath() => _current switch
    {
        FilePlaylistItem f => f.Path,
        ImagePlaylistItem i => i.Path,
        SubtitlePlaylistItem s => s.Path,
        YouTubePlaylistItem y when Preparer.IsPrepared(y.VideoId, y.VideoStreamDescriptor, y.AudioStreamDescriptor, y.IncludeThumbnail) =>
            Preparer.AssetPathFor(y.VideoId, y.VideoStreamDescriptor, y.AudioStreamDescriptor, y.IncludeThumbnail),
        _ => null,
    };

    internal void PublishContainerInfo(MediaContainerInfo info)
    {
        DetailRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowContainer, info.FormatName));
        if (info.Duration > TimeSpan.Zero)
            DetailRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowDuration, FormatDuration(info.Duration)));
        if (info.BitRate > 0)
            DetailRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowBitrate, FormatBitRate(info.BitRate)));
        if (info.FileSizeBytes > 0)
            DetailRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowFileSize, FormatSize(info.FileSizeBytes)));

        var video = info.Streams.FirstOrDefault(s => s.Kind == MediaStreamKind.Video && !s.IsAttachedPicture);
        if (video is { Width: > 0 })
        {
            DetailRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowResolution, $"{video.Width}×{video.Height}"));
            if (video.FrameRate.Numerator > 0 && video.FrameRate.Denominator > 0)
                DetailRows.Add(new MediaPropertyRow(
                    Strings.MediaPropertiesRowFrameRate,
                    $"{(double)video.FrameRate.Numerator / video.FrameRate.Denominator:0.###} fps"));
        }

        StreamLines.Clear();
        foreach (var stream in info.Streams)
        {
            var line = stream.ToDisplayString();
            if (stream.BitRate > 0)
                line += $" · {FormatBitRate(stream.BitRate)}";
            StreamLines.Add(line);
        }

        HasStreamLines = StreamLines.Count > 0;
    }

    // ---------------------------------------------------------------- Tracks (file items)

    public ObservableCollection<MediaPropertiesAudioTrackOption> AudioTrackOptions { get; } = [];

    [ObservableProperty]
    private MediaPropertiesAudioTrackOption? _selectedAudioTrack;

    [ObservableProperty]
    private bool _hasMultipleAudioTracks;

    /// <summary>True when the file has embedded subtitle streams (or already-configured subtitles) - gates the
    /// subtitle picker in the dialog, which is embedded-track-based (nothing to offer otherwise).</summary>
    [ObservableProperty]
    private bool _hasSubtitleTracks;

    /// <summary>True for a file item with neither a multi-track audio choice nor subtitles - the Tracks tab
    /// would otherwise be blank, so it shows an explanatory hint instead.</summary>
    public bool HasNoSelectableTracks => IsFileItem && !HasMultipleAudioTracks && !HasSubtitleTracks;

    partial void OnHasMultipleAudioTracksChanged(bool value) => OnPropertyChanged(nameof(HasNoSelectableTracks));
    partial void OnHasSubtitleTracksChanged(bool value) => OnPropertyChanged(nameof(HasNoSelectableTracks));

    public string SubtitleSummary => _current switch
    {
        FilePlaylistItem { Subtitles.Count: > 0 } f =>
            Strings.Format(nameof(Strings.MediaPropertiesSubtitleCountFormat), f.Subtitles.Count),
        FilePlaylistItem => Strings.MediaPropertiesSubtitlesNone,
        _ => string.Empty,
    };

    /// <summary>Fills the audio-track picker for file items (called by the view after open; tests call
    /// <see cref="PublishAudioTracks"/> directly).</summary>
    public async Task LoadAudioTracksAsync()
    {
        if (_current is not FilePlaylistItem file)
            return;
        var tracks = await Playback.CueMediaProbe.TryProbeAudioTracksAsync(file.Path).ConfigureAwait(true);
        PublishAudioTracks(tracks);
    }

    /// <summary>Probes the file for embedded subtitle streams to decide whether the subtitle picker shows
    /// (tests call <see cref="PublishSubtitleAvailability"/> directly). Already-configured subtitles keep it
    /// available even if the embedded probe finds none.</summary>
    public async Task LoadSubtitleTracksAsync()
    {
        if (_current is not FilePlaylistItem file)
            return;
        if (file.Subtitles.Count > 0)
        {
            PublishSubtitleAvailability(true);
            return;
        }
        var tracks = await Playback.CueMediaProbe.TryProbeSubtitleTracksAsync(file.Path).ConfigureAwait(true);
        PublishSubtitleAvailability(tracks.Count > 0);
    }

    internal void PublishSubtitleAvailability(bool hasTracks) => HasSubtitleTracks = hasTracks;

    internal void PublishAudioTracks(IReadOnlyList<MediaStreamInfo> tracks)
    {
        if (_current is not FilePlaylistItem file)
            return;

        _suppressTrackApply = true;
        try
        {
            AudioTrackOptions.Clear();
            AudioTrackOptions.Add(new MediaPropertiesAudioTrackOption(null, Strings.AudioTrackAutomaticLabel));
            foreach (var track in tracks)
                AudioTrackOptions.Add(new MediaPropertiesAudioTrackOption(track.Index, track.ToDisplayString()));

            SelectedAudioTrack =
                AudioTrackOptions.FirstOrDefault(o => o.Index == file.AudioTrackIndex) ?? AudioTrackOptions[0];
            HasMultipleAudioTracks = tracks.Count >= 2;
        }
        finally
        {
            _suppressTrackApply = false;
        }
    }

    partial void OnSelectedAudioTrackChanged(MediaPropertiesAudioTrackOption? value)
    {
        if (_suppressTrackApply || value is null || _current is not FilePlaylistItem file
            || file.AudioTrackIndex == value.Index)
            return;
        Replace(file with { AudioTrackIndex = value.Index });
    }

    /// <summary>Stores the subtitle selection produced by the subtitle picker child dialog.</summary>
    public void ApplySubtitles(IReadOnlyList<CueSubtitleSelection> selections)
    {
        if (_current is FilePlaylistItem file)
            Replace(file with { Subtitles = selections });
    }

    // ---------------------------------------------------------------- Scene (MMD items)

    public ObservableCollection<MediaPropertyRow> SceneRows { get; } = [];

    [ObservableProperty]
    private string? _bakeStatus;

    [ObservableProperty]
    private double _bakeProgress;

    [ObservableProperty]
    private bool _isBaking;

    public bool CanBakePhysics =>
        _current is MMDPlaylistItem { Physics: true, MotionPath.Length: > 0 } mmd
        && FileExists(mmd.ModelPath) && FileExists(mmd.MotionPath) && !IsBaking;

    /// <summary>Explicit pre-bake from the dialog: loads the documents on a worker, bakes the full
    /// physics timeline with progress, and stores it in the shared cache so playback opens it directly.</summary>
    [RelayCommand(CanExecute = nameof(CanBakePhysics))]
    private async Task BakePhysicsAsync()
    {
        if (_current is not MMDPlaylistItem { MotionPath: { Length: > 0 } motionPath } mmd)
            return;

        IsBaking = true;
        BakeProgress = 0;
        BakeStatus = Strings.MediaPropertiesBakeInProgress;
        NotifyBakeStateChanged();
        try
        {
            var baked = await Task.Run(async () =>
            {
                var model = PMXDocument.Load(mmd.ModelPath);
                var motion = VMDDocument.Load(motionPath);
                return await MMDPhysicsBakeCache.BakeAsync(
                    mmd.ModelPath, motionPath, model, motion,
                    progress: p => Avalonia.Threading.Dispatcher.UIThread.Post(() => BakeProgress = p)).ConfigureAwait(false);
            }).ConfigureAwait(true);

            BakeProgress = 1;
            BakeStatus = baked is not null
                ? Strings.MediaPropertiesBakeDone
                : Strings.MediaPropertiesBakeStatusNoMotion;
        }
        catch (Exception ex)
        {
            BakeStatus = Strings.Format(nameof(Strings.MediaPropertiesBakeFailedFormat), ex.Message);
        }
        finally
        {
            IsBaking = false;
            NotifyBakeStateChanged();
        }
    }

    private void RefreshBakeStatus()
    {
        if (_current is not MMDPlaylistItem mmd)
        {
            BakeStatus = null;
            return;
        }

        BakeStatus = mmd switch
        {
            { Physics: false } => Strings.MediaPropertiesBakeStatusDisabled,
            { MotionPath: null or "" } => Strings.MediaPropertiesBakeStatusNoMotion,
            _ when MMDPhysicsBakeCache.IsCached(mmd.ModelPath, mmd.MotionPath!) => Strings.MediaPropertiesBakeStatusCached,
            _ => Strings.MediaPropertiesBakeStatusNotCached,
        };
    }

    private void NotifyBakeStateChanged()
    {
        OnPropertyChanged(nameof(CanBakePhysics));
        BakePhysicsCommand.NotifyCanExecuteChanged();
    }

    // ---------------------------------------------------------------- Streams (YouTube items)

    public ObservableCollection<MediaPropertyRow> YouTubeRows { get; } = [];

    // ---------------------------------------------------------------- shared

    /// <summary>Accepts the item produced by a nested editor dialog (scene / streams). The editors
    /// preserve <see cref="PlaylistItem.Id"/>; a mismatching id is rejected (stale dialog result).</summary>
    public void ReplaceItem(PlaylistItem updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (updated.Id != _current.Id)
            return;
        Replace(updated);
    }

    /// <summary>The dialog result for OK.</summary>
    public PlaylistItem BuildResult() => _current;

    private void Replace(PlaylistItem updated)
    {
        _current = updated;
        RebuildStaticState();
    }

    /// <summary>Re-derives every displayed fact from <see cref="_current"/> (initial build and after
    /// each edit). Probed container facts are merged back in by <see cref="LoadDetailsAsync"/>.</summary>
    private void RebuildStaticState()
    {
        DetailRows.Clear();
        DetailRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowName, _current.DisplayName));
        DetailRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowKind, KindLabel(_current)));
        foreach (var row in StaticRowsFor(_current))
            DetailRows.Add(row);

        SceneRows.Clear();
        if (_current is MMDPlaylistItem mmd)
        {
            SceneRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowModel, mmd.ModelPath));
            SceneRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowMotion, mmd.MotionPath ?? Strings.MediaPropertiesValueNone));
            SceneRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowCameraMotion, mmd.CameraMotionPath ?? Strings.MediaPropertiesValueNone));
            SceneRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowRenderSize, $"{mmd.RenderWidth}×{mmd.RenderHeight}"));
            SceneRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowCamera,
                $"d={mmd.CameraDistance:0.#} · t=({mmd.CameraTargetX:0.#}, {mmd.CameraTargetY:0.#}, {mmd.CameraTargetZ:0.#}) · " +
                $"r=({mmd.CameraRotationXDeg:0.#}°, {mmd.CameraRotationYDeg:0.#}°, {mmd.CameraRotationZDeg:0.#}°) · " +
                $"fov={mmd.CameraFovDeg:0.#}°"));
            SceneRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowPhysics, mmd.Physics ? Strings.MediaPropertiesValueOn : Strings.MediaPropertiesValueOff));
            SceneRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowAntialias, mmd.Antialias ? Strings.MediaPropertiesValueOn : Strings.MediaPropertiesValueOff));
            RefreshBakeStatus();
        }

        YouTubeRows.Clear();
        if (_current is YouTubePlaylistItem yt)
        {
            YouTubeRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowVideoId, yt.VideoId));
            if (yt.Author is { Length: > 0 })
                YouTubeRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowAuthor, yt.Author));
            YouTubeRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowVideoStream,
                yt.AudioOnly ? Strings.MediaPropertiesValueNone : yt.VideoStreamDescriptor ?? Strings.MediaPropertiesValueNone));
            YouTubeRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowAudioStream, yt.AudioStreamDescriptor ?? Strings.MediaPropertiesValueNone));
            if (yt.SubtitleLanguage is { Length: > 0 })
                YouTubeRows.Add(new MediaPropertyRow(Strings.MediaPropertiesRowSubtitleLanguage, yt.SubtitleLanguage));
            YouTubeRows.Add(new MediaPropertyRow(
                Strings.MediaPropertiesRowCacheState,
                Preparer.IsPrepared(yt.VideoId, yt.VideoStreamDescriptor, yt.AudioStreamDescriptor, yt.IncludeThumbnail)
                    ? Strings.MediaPropertiesCacheStateCached
                    : Strings.MediaPropertiesCacheStateNotCached));
        }

        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(IsFileItem));
        OnPropertyChanged(nameof(IsMMDItem));
        OnPropertyChanged(nameof(IsYouTubeItem));
        OnPropertyChanged(nameof(SubtitleSummary));
        NotifyBakeStateChanged();
    }

    private static string KindLabel(PlaylistItem item) => item switch
    {
        FilePlaylistItem => Strings.MediaPropertiesKindFile,
        ImagePlaylistItem => Strings.MediaPropertiesKindImage,
        SubtitlePlaylistItem => Strings.MediaPropertiesKindSubtitle,
        TextPlaylistItem => Strings.MediaPropertiesKindText,
        MMDPlaylistItem => Strings.MediaPropertiesKindMMD,
        YouTubePlaylistItem => Strings.MediaPropertiesKindYouTube,
        NDIInputPlaylistItem => Strings.MediaPropertiesKindNDI,
        PortAudioInputPlaylistItem => Strings.MediaPropertiesKindPortAudio,
        _ => item.GetType().Name,
    };

    private static IEnumerable<MediaPropertyRow> StaticRowsFor(PlaylistItem item)
    {
        switch (item)
        {
            case FilePlaylistItem f:
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowLocation, f.Path);
                break;
            case ImagePlaylistItem i:
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowLocation, i.Path);
                break;
            case SubtitlePlaylistItem s:
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowLocation, s.Path);
                break;
            case TextPlaylistItem t:
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowResolution, $"{t.CanvasWidth}×{t.CanvasHeight}");
                break;
            case MMDPlaylistItem m:
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowLocation, m.ModelPath);
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowResolution, $"{m.RenderWidth}×{m.RenderHeight}");
                break;
            case YouTubePlaylistItem y:
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowVideoId, y.VideoId);
                if (y.DurationSeconds is { } seconds and > 0)
                    yield return new MediaPropertyRow(
                        Strings.MediaPropertiesRowDuration, FormatDuration(TimeSpan.FromSeconds(seconds)));
                break;
            case NDIInputPlaylistItem n:
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowLocation, n.SourceName);
                break;
            case PortAudioInputPlaylistItem p:
                yield return new MediaPropertyRow(Strings.MediaPropertiesRowLocation, p.DeviceName);
                yield return new MediaPropertyRow(
                    Strings.MediaPropertiesRowAudioStream, $"{p.Channels}ch {p.SampleRate} Hz");
                break;
        }
    }

    internal static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    internal static string FormatBitRate(long bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000 => $"{bitsPerSecond / 1_000_000.0:0.#} Mbit/s",
        >= 1_000 => $"{bitsPerSecond / 1_000.0:0.#} kbit/s",
        _ => $"{bitsPerSecond} bit/s",
    };

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.##} GiB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.#} MiB",
        >= 1_024 => $"{bytes / 1_024.0:0.#} KiB",
        _ => $"{bytes} B",
    };
}

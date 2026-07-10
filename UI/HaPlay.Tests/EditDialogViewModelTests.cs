using HaPlay.ViewModels.Dialogs;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

public sealed class EditDialogViewModelTests
{
    [Fact]
    public void LocalVideo_LoadFromExisting_SwitchesToEditMode()
    {
        var vm = new AddLocalVideoOutputDialogViewModel();
        Assert.False(vm.IsEditing);
        Assert.Equal("Add local video output", vm.DialogTitle);
        Assert.Equal("Add", vm.PrimaryButtonLabel);

        var existing = new LocalVideoOutputDefinition(
            Guid.NewGuid(), "Program", VideoOutputEngine.AvaloniaOpenGl, VideoSurfaceMode.Windowed,
            ScreenIndex: 0, WindowWidth: 1920, WindowHeight: 1080);

        vm.LoadFromExisting(existing);

        Assert.True(vm.IsEditing);
        Assert.True(vm.EngineLockedByEdit);
        Assert.Equal("Edit local video output", vm.DialogTitle);
        Assert.Equal("Save", vm.PrimaryButtonLabel);
        Assert.Equal("Program", vm.DisplayName);
        Assert.Equal(VideoOutputEngine.AvaloniaOpenGl, vm.Engine);
        Assert.Equal(VideoSurfaceMode.Windowed, vm.SurfaceMode);
        Assert.Equal(1920, vm.WindowWidth);
        Assert.Equal(1080, vm.WindowHeight);
    }

    [Fact]
    public void LocalVideo_VideoFit_RoundTripsThroughLoadAndCommit()
    {
        var vm = new AddLocalVideoOutputDialogViewModel();
        vm.Screens.Add(new ScreenListItem { Index = 0, Label = "Primary" });
        vm.SelectedScreen = vm.Screens[0];

        // Default is Letterbox before any load (the common case for a fresh output).
        Assert.Equal(LocalVideoFit.Letterbox, vm.SelectedVideoFit);

        vm.LoadFromExisting(new LocalVideoOutputDefinition(
            Guid.NewGuid(), "Program", VideoOutputEngine.SDLOpenGl, VideoSurfaceMode.Windowed,
            ScreenIndex: 0, WindowWidth: 1920, WindowHeight: 1080, VideoFit: LocalVideoFit.Cover));

        Assert.Equal(LocalVideoFit.Cover, vm.SelectedVideoFit);

        // Operator switches it to Stretch; the committed definition carries the new fit.
        vm.SelectedVideoFit = LocalVideoFit.Stretch;
        var committed = vm.TryCommit();

        Assert.NotNull(committed);
        Assert.Equal(LocalVideoFit.Stretch, committed!.VideoFit);
    }

    [Fact]
    public void LocalVideo_TryCommit_PreservesIdAndCloneOfId()
    {
        var vm = new AddLocalVideoOutputDialogViewModel();
        var screens = new[] { new ScreenListItem { Index = 0, Label = "Primary" } };
        // Use the property setter path the real dialog uses (InitializeScreens needs Avalonia Screen);
        // for tests we drop directly into the collection.
        vm.Screens.Add(screens[0]);
        vm.SelectedScreen = screens[0];

        var id = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var parent = new LocalVideoOutputDefinition(
            parentId, "Program", VideoOutputEngine.SDLOpenGl, VideoSurfaceMode.FullScreen,
            ScreenIndex: 0, WindowWidth: null, WindowHeight: null);
        // Real flow: ShowEditLocalVideoAsync calls InitializeCloneParents before LoadFromExisting so
        // the dropdown can resolve the saved CloneOfId to a choice. Mirror that here.
        vm.InitializeCloneParents([parent]);
        vm.LoadFromExisting(new LocalVideoOutputDefinition(
            id, "Clone", VideoOutputEngine.SDLOpenGl, VideoSurfaceMode.Windowed,
            ScreenIndex: 0, WindowWidth: 640, WindowHeight: 360, CloneOfId: parentId));

        // User changes the display name only.
        vm.DisplayName = "Renamed";
        var committed = vm.TryCommit();

        Assert.NotNull(committed);
        Assert.Equal(id, committed!.Id);
        Assert.Equal("Renamed", committed.DisplayName);
        Assert.Equal(parentId, committed.CloneOfId);
    }

    [Fact]
    public void LocalVideo_InitializeExistingOutputNames_AutoIncrementsDefaultDisplayName()
    {
        var vm = new AddLocalVideoOutputDialogViewModel();

        vm.InitializeExistingOutputNames(["Program output", "Program output 2"]);

        Assert.Equal("Program output 3", vm.DisplayName);
    }

    [Fact]
    public void LocalVideo_TryCommit_RejectsDuplicateDisplayName()
    {
        var vm = new AddLocalVideoOutputDialogViewModel();
        var screen = new ScreenListItem { Index = 0, Label = "Primary" };
        vm.Screens.Add(screen);
        vm.SelectedScreen = screen;
        vm.InitializeExistingOutputNames(["Program output"]);
        vm.DisplayName = "Program output";

        var committed = vm.TryCommit();

        Assert.Null(committed);
        Assert.Contains("Program output", vm.ValidationMessage);
    }

    [Fact]
    public void LocalVideo_LoadFromExisting_MissingParent_FallsBackToNone()
    {
        var vm = new AddLocalVideoOutputDialogViewModel();
        vm.Screens.Add(new ScreenListItem { Index = 0, Label = "Primary" });
        vm.SelectedScreen = vm.Screens[0];
        vm.InitializeCloneParents(Array.Empty<LocalVideoOutputDefinition>());

        // Project file references a parent that no longer exists - dialog should degrade gracefully to
        // "None - independent output" so the user can either pick another parent or save as standalone.
        var saved = new LocalVideoOutputDefinition(
            Guid.NewGuid(), "OrphanedClone", VideoOutputEngine.SDLOpenGl, VideoSurfaceMode.Windowed,
            ScreenIndex: 0, WindowWidth: 320, WindowHeight: 240, CloneOfId: Guid.NewGuid());
        vm.LoadFromExisting(saved);

        Assert.Same(CloneParentChoice.None, vm.SelectedCloneParent);

        var committed = vm.TryCommit();
        Assert.NotNull(committed);
        Assert.Null(committed!.CloneOfId);
    }

    [Fact]
    public void NDI_LoadFromExisting_PreservesPixelFormatAndResolutionLock()
    {
        var vm = new AddNDIOutputDialogViewModel();
        var id = Guid.NewGuid();
        vm.LoadFromExisting(new NDIOutputDefinition(
            id, "NDI Prog", "Studio (Cam1)", Groups: "Studio",
            NDIOutputStreamMode.VideoAndAudio, AudioChannelCount: 4, AudioSampleRate: 48000,
            PixelFormatLock: PixelFormat.Uyvy, ResolutionLockWidth: 1280, ResolutionLockHeight: 720));

        Assert.True(vm.IsEditing);
        Assert.Equal("Edit NDI output", vm.DialogTitle);

        // User changes the source name only.
        vm.SourceName = "Studio (Cam2)";
        var committed = vm.TryCommit();

        Assert.NotNull(committed);
        Assert.Equal(id, committed!.Id);
        Assert.Equal("Studio (Cam2)", committed.SourceName);
        // Forward-compat locks must survive the edit even though the dialog doesn't expose them yet -
        // otherwise the first edit silently strips the lock.
        Assert.Equal(PixelFormat.Uyvy, committed.PixelFormatLock);
        Assert.Equal(1280, committed.ResolutionLockWidth);
        Assert.Equal(720, committed.ResolutionLockHeight);
    }

    /// <summary>Phase C polish (§4.3.5) - the dialog now exposes editable pixel-format and resolution
    /// locks. Selecting an entry on a fresh "Add NDI" flow must round-trip onto the produced
    /// <see cref="NDIOutputDefinition"/>, otherwise the runtime <see cref="LockedFormatVideoOutput"/>
    /// wrapper has nothing to act on.</summary>
    [Fact]
    public void NDI_TryCommit_PersistsSelectedPixelFormatAndResolutionLocks()
    {
        var vm = new AddNDIOutputDialogViewModel
        {
            DisplayName = "NDI Locked",
            SourceName = "Studio (Cam1)",
            SelectedPixelFormat = NDIPixelFormatChoice.FromPixelFormat(PixelFormat.Uyvy)!,
            SelectedResolution = NDIResolutionChoice.All.First(r => r.Width == 1920 && r.Height == 1080),
        };

        var committed = vm.TryCommit();

        Assert.NotNull(committed);
        Assert.Equal(PixelFormat.Uyvy, committed!.PixelFormatLock);
        Assert.Equal(1920, committed.ResolutionLockWidth);
        Assert.Equal(1080, committed.ResolutionLockHeight);
    }

    [Fact]
    public void NDI_TryCommit_RejectsDuplicateDisplayName()
    {
        var vm = new AddNDIOutputDialogViewModel
        {
            SourceName = "Studio (Cam1)",
        };
        vm.InitializeExistingOutputNames(["NDI program"]);
        vm.DisplayName = "NDI program";

        var committed = vm.TryCommit();

        Assert.Null(committed);
        Assert.Contains("NDI program", vm.ValidationMessage);
    }

    /// <summary>"Auto" entries map back to nullable lock fields. Without this, every NDI output created
    /// through the dialog would pin a lock even when the operator left both dropdowns on Auto.</summary>
    [Fact]
    public void NDI_TryCommit_AutoChoices_ProduceNullLocks()
    {
        var vm = new AddNDIOutputDialogViewModel
        {
            DisplayName = "NDI Auto",
            SourceName = "Studio (Cam1)",
            // Defaults are Auto, but spell it out so the test is self-documenting.
            SelectedPixelFormat = NDIPixelFormatChoice.Auto,
            SelectedResolution = NDIResolutionChoice.Auto,
        };

        var committed = vm.TryCommit();

        Assert.NotNull(committed);
        Assert.Null(committed!.PixelFormatLock);
        Assert.Null(committed.ResolutionLockWidth);
        Assert.Null(committed.ResolutionLockHeight);
    }

    [Fact]
    public void NDI_TryCommit_CustomResolution_PersistsEditedEvenValues()
    {
        var vm = new AddNDIOutputDialogViewModel
        {
            DisplayName = "NDI Custom",
            SourceName = "Studio (Cam1)",
            SelectedResolution = NDIResolutionChoice.Custom,
            CustomResolutionWidth = 1920,
            CustomResolutionHeight = 1200,
        };
        Assert.True(vm.ShowCustomResolution);

        var committed = vm.TryCommit();

        Assert.NotNull(committed);
        Assert.Equal(1920, committed!.ResolutionLockWidth);
        Assert.Equal(1200, committed.ResolutionLockHeight);
    }

    [Fact]
    public void NDI_TryCommit_CustomResolution_RejectsOddDimensions()
    {
        var vm = new AddNDIOutputDialogViewModel
        {
            DisplayName = "NDI Custom",
            SourceName = "Studio (Cam1)",
            SelectedResolution = NDIResolutionChoice.Custom,
            CustomResolutionWidth = 1921, // odd - NDI senders require even dimensions
            CustomResolutionHeight = 1080,
        };

        var committed = vm.TryCommit();

        Assert.Null(committed);
        Assert.False(string.IsNullOrWhiteSpace(vm.ValidationMessage));
    }

    [Fact]
    public void NDI_LoadFromExisting_NonPresetLock_SelectsCustomAndPopulatesFields()
    {
        var existing = new NDIOutputDefinition(
            Guid.NewGuid(), "NDI", "Studio (Cam1)", null, NDIOutputStreamMode.VideoAndAudio, 2, 48_000,
            ResolutionLockWidth: 2560, ResolutionLockHeight: 1440);

        var vm = new AddNDIOutputDialogViewModel();
        vm.LoadFromExisting(existing);

        Assert.True(vm.SelectedResolution.IsCustom);
        Assert.True(vm.ShowCustomResolution);
        Assert.Equal(2560, vm.CustomResolutionWidth);
        Assert.Equal(1440, vm.CustomResolutionHeight);

        var committed = vm.TryCommit();
        Assert.Equal(2560, committed!.ResolutionLockWidth);
        Assert.Equal(1440, committed.ResolutionLockHeight);
    }

    [Fact]
    public void PortAudio_LoadFromExisting_SwitchesToEditMode()
    {
        var vm = new AddPortAudioOutputDialogViewModel();
        Assert.False(vm.IsEditing);

        var existing = new PortAudioOutputDefinition(
            Guid.NewGuid(), "Stage", 0, "Alsa", 1, "dev", 4, 96000);
        // Don't call ReloadHostApis (touches real PortAudio); LoadFromExisting calls it internally.
        // On CI / no-device hosts that returns empty collections; the test only verifies the title /
        // button / field state, which doesn't depend on device enumeration.
        vm.LoadFromExisting(existing);

        Assert.True(vm.IsEditing);
        Assert.Equal("Edit audio output", vm.DialogTitle);
        Assert.Equal("Save", vm.PrimaryButtonLabel);
        Assert.Equal("Stage", vm.DisplayName);
        Assert.Equal(4, vm.ChannelCount);
        Assert.Equal(96000, vm.SampleRate);
    }

    [Fact]
    public void PortAudio_TryCommit_RejectsDuplicateDisplayNameBeforeDeviceValidation()
    {
        var vm = new AddPortAudioOutputDialogViewModel();
        vm.InitializeExistingOutputNames(["Main speakers"]);
        vm.DisplayName = "Main speakers";

        var committed = vm.TryCommit();

        Assert.Null(committed);
        Assert.Contains("Main speakers", vm.ValidationMessage);
    }
}

using HaPlay.OutputPreview;
using HaPlay.ViewModels;
using S.Media.Audio.PortAudio;
using Xunit;

namespace HaPlay.Tests;

public sealed class OutputManagementViewModelTests
{
    /// <summary>Phase E (§8.1) — the sparkline ring on <see cref="OutputLineViewModel"/> stores
    /// per-tick deltas. Three ticks of growing cumulative counters must produce three positive
    /// samples whose peak matches the largest delta.</summary>
    [Fact]
    public void OutputLineViewModel_RecordSparklineSample_StoresPerTickDeltas()
    {
        var line = new OutputLineViewModel(
            new PortAudioOutputDefinition(Guid.NewGuid(), "Spark", 0, "Alsa", 1, "d", 2, 48000),
            _ => { });

        line.RecordSparklineSample(videoSubmittedTotal: 60, audioEnqueuedTotal: 100);
        line.RecordSparklineSample(videoSubmittedTotal: 120, audioEnqueuedTotal: 200);
        line.RecordSparklineSample(videoSubmittedTotal: 240, audioEnqueuedTotal: 350);

        var samples = line.SparklineSamples;
        Assert.Equal(3, samples.Count);
        Assert.Equal(60 + 100, samples[0]); // first tick: 60 frames + 100 chunks
        Assert.Equal(60 + 100, samples[1]); // second tick: same delta (60+100)
        Assert.Equal(120 + 150, samples[2]); // third tick: 120 frames + 150 chunks
        Assert.Equal(270, line.SparklinePeakSample);
        Assert.Equal(270, line.SparklineLastSample);
    }

    /// <summary>Sparkline reset clears the ring + last-counters so a re-Play after Stop starts fresh
    /// rather than emitting a giant first-tick spike from the cumulative drift.</summary>
    [Fact]
    public void OutputLineViewModel_ResetSparkline_ClearsRingAndLastCounters()
    {
        var line = new OutputLineViewModel(
            new PortAudioOutputDefinition(Guid.NewGuid(), "Spark", 0, "Alsa", 1, "d", 2, 48000),
            _ => { });
        line.RecordSparklineSample(60, 100);
        line.RecordSparklineSample(120, 200);
        Assert.Equal(2, line.SparklineSamples.Count);

        line.ResetSparkline();
        Assert.Empty(line.SparklineSamples);
        Assert.Equal(0, line.SparklinePeakSample);

        // First post-reset sample uses the FULL counter values, not deltas relative to the pre-reset state.
        line.RecordSparklineSample(50, 70);
        Assert.Single(line.SparklineSamples);
        Assert.Equal(120, line.SparklineSamples[0]);
    }

    [Fact]
    public void ReplaceDefinitionsForLoad_EmptyToPopulated_PopulatesOutputs()
    {
        var vm = new OutputManagementViewModel();
        var paId = Guid.NewGuid();
        var ndiId = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new PortAudioOutputDefinition(paId, "PA", 0, "Alsa", 1, "dev", 2, 48000),
            new NDIOutputDefinition(ndiId, "NDI", "src", null, NDIOutputStreamMode.VideoAndAudio, 2, 48000),
        });

        Assert.Equal(2, vm.Outputs.Count);
        Assert.Equal(paId, vm.Outputs[0].Definition.Id);
        Assert.Equal(ndiId, vm.Outputs[1].Definition.Id);
    }

    [Fact]
    public void ReplaceDefinitionsForLoad_ReplacesEntirely()
    {
        var vm = new OutputManagementViewModel();
        var firstId = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new PortAudioOutputDefinition(firstId, "First", 0, "Alsa", 1, "d", 2, 48000),
        });
        Assert.Single(vm.Outputs);

        var secondId = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new PortAudioOutputDefinition(secondId, "Second", 0, "Alsa", 1, "d", 2, 48000),
        });
        Assert.Single(vm.Outputs);
        Assert.Equal(secondId, vm.Outputs[0].Definition.Id);
        Assert.Equal("Second", vm.Outputs[0].Definition.DisplayName);
    }

    [Fact]
    public async Task ReconfigureLineAsync_NoRunningRuntime_UpdatesDefinitionInPlace()
    {
        var vm = new OutputManagementViewModel();
        var id = Guid.NewGuid();
        var original = new PortAudioOutputDefinition(id, "PA", 0, "Alsa", 1, "dev", 2, 48000);
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[] { original });

        var line = vm.Outputs[0];
        Assert.Equal(48000, ((PortAudioOutputDefinition)line.Definition).SampleRate);

        // No PortAudio runtime is started in this VM (ReplaceDefinitionsForLoad never spins up runtimes
        // by design — Phase B's load orchestration does that). The reconfigure should still update the
        // definition on the line VM so consumers observe the new values.
        var updated = original with { SampleRate = 96000, ChannelCount = 4 };
        await vm.ReconfigureLineAsync(line, updated);

        var pa = Assert.IsType<PortAudioOutputDefinition>(line.Definition);
        Assert.Equal(96000, pa.SampleRate);
        Assert.Equal(4, pa.ChannelCount);
        Assert.Equal(id, pa.Id);
    }

    [Fact]
    public async Task ReconfigureLineAsync_MismatchedId_Throws()
    {
        var vm = new OutputManagementViewModel();
        var id = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new PortAudioOutputDefinition(id, "PA", 0, "Alsa", 1, "d", 2, 48000),
        });

        var line = vm.Outputs[0];
        var foreign = new PortAudioOutputDefinition(Guid.NewGuid(), "X", 0, "Alsa", 1, "d", 2, 48000);
        await Assert.ThrowsAsync<ArgumentException>(() => vm.ReconfigureLineAsync(line, foreign));
    }

    [Fact]
    public async Task ReconfigureLineAsync_KindChange_Throws()
    {
        var vm = new OutputManagementViewModel();
        var id = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new PortAudioOutputDefinition(id, "PA", 0, "Alsa", 1, "d", 2, 48000),
        });

        var line = vm.Outputs[0];
        // Same Id, different kind. The runtime mapping doesn't work — must be rejected so callers can
        // surface the right error instead of silently doing nothing.
        var swapped = new NDIOutputDefinition(id, "PA", "src", null, NDIOutputStreamMode.VideoAndAudio, 2, 48000);
        await Assert.ThrowsAsync<ArgumentException>(() => vm.ReconfigureLineAsync(line, swapped));
    }

    [Fact]
    public async Task ReconfigureLineAsync_RaisesHooks_AroundDefinitionSwap()
    {
        var vm = new OutputManagementViewModel();
        var id = Guid.NewGuid();
        var original = new PortAudioOutputDefinition(id, "PA", 0, "Alsa", 1, "d", 2, 48000);
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[] { original });

        var line = vm.Outputs[0];
        var updated = original with { SampleRate = 96000 };
        var events = new List<string>();

        vm.OutputLineReconfiguringAsync += l =>
        {
            var pa = Assert.IsType<PortAudioOutputDefinition>(l.Definition);
            events.Add($"pre:{pa.SampleRate}");
            return Task.CompletedTask;
        };
        vm.OutputLineReconfiguredAsync += l =>
        {
            var pa = Assert.IsType<PortAudioOutputDefinition>(l.Definition);
            events.Add($"post:{pa.SampleRate}");
            return Task.CompletedTask;
        };

        await vm.ReconfigureLineAsync(line, updated);

        Assert.Equal(new[] { "pre:48000", "post:96000" }, events);
    }

    [Fact]
    public void NotifyLocalPreviewResized_UpdatesWindowedLocalVideoDefinition()
    {
        var vm = new OutputManagementViewModel();
        var id = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new LocalVideoOutputDefinition(id, "Program", VideoOutputEngine.SdlOpenGl,
                VideoSurfaceMode.Windowed, 0, 1280, 720),
        });

        var line = vm.Outputs[0];
        vm.NotifyLocalPreviewResized(line, 1920, 1080);

        var local = Assert.IsType<LocalVideoOutputDefinition>(line.Definition);
        Assert.Equal(1920, local.WindowWidth);
        Assert.Equal(1080, local.WindowHeight);
    }

    [Fact]
    public void NotifyLocalPreviewResized_IgnoresFullscreenAndTooSmallSizes()
    {
        var vm = new OutputManagementViewModel();
        var fullscreenId = Guid.NewGuid();
        var windowedId = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new LocalVideoOutputDefinition(fullscreenId, "Program", VideoOutputEngine.SdlOpenGl,
                VideoSurfaceMode.FullScreen, 0, null, null),
            new LocalVideoOutputDefinition(windowedId, "Preview", VideoOutputEngine.AvaloniaOpenGl,
                VideoSurfaceMode.Windowed, 0, 1280, 720),
        });

        vm.NotifyLocalPreviewResized(vm.Outputs[0], 1920, 1080);
        vm.NotifyLocalPreviewResized(vm.Outputs[1], 200, 100);

        var fullscreen = Assert.IsType<LocalVideoOutputDefinition>(vm.Outputs[0].Definition);
        Assert.Null(fullscreen.WindowWidth);
        Assert.Null(fullscreen.WindowHeight);
        var windowed = Assert.IsType<LocalVideoOutputDefinition>(vm.Outputs[1].Definition);
        Assert.Equal(1280, windowed.WindowWidth);
        Assert.Equal(720, windowed.WindowHeight);
    }

    [Fact]
    public void PortAudioResolveCurrentOutputDevice_PrefersSavedNameOverStaleGlobalIndex()
    {
        var saved = new PortAudioOutputDefinition(
            Guid.NewGuid(),
            "Main speakers",
            HostApiIndex: 0,
            HostApiName: "ALSA",
            GlobalDeviceIndex: 5,
            DeviceName: "Main speakers",
            ChannelCount: 2,
            SampleRate: 48000);
        var devices = new[]
        {
            new PortAudioOutputDeviceEntry(5, 0, "Different speakers", 2, 48000, false),
            new PortAudioOutputDeviceEntry(12, 0, "Main speakers", 2, 48000, false),
        };
        var hostApis = new[]
        {
            new PortAudioHostApiEntry(0, "ALSA", 0, 2, -1),
        };

        var resolved = PortAudioOutputRuntime.ResolveCurrentOutputDevice(saved, devices, hostApis);

        Assert.Equal(12, resolved.GlobalDeviceIndex);
        Assert.Equal("Main speakers", resolved.DeviceName);
    }

    [Fact]
    public void PortAudioResolveCurrentOutputDevice_DoesNotFallbackToWrongNamedSavedIndex()
    {
        var saved = new PortAudioOutputDefinition(
            Guid.NewGuid(),
            "Main speakers",
            HostApiIndex: 0,
            HostApiName: "ALSA",
            GlobalDeviceIndex: 5,
            DeviceName: "Main speakers",
            ChannelCount: 2,
            SampleRate: 48000);
        var devices = new[]
        {
            new PortAudioOutputDeviceEntry(5, 0, "Different speakers", 2, 48000, false),
        };
        var hostApis = new[]
        {
            new PortAudioHostApiEntry(0, "ALSA", 0, 1, -1),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PortAudioOutputRuntime.ResolveCurrentOutputDevice(saved, devices, hostApis));
        Assert.Contains("device 'Main speakers' was not found", ex.Message);
    }

    [Fact]
    public void PortAudioResolveCurrentOutputDevice_ReportsChannelMismatchForNamedDevice()
    {
        var saved = new PortAudioOutputDefinition(
            Guid.NewGuid(),
            "Main speakers",
            HostApiIndex: 0,
            HostApiName: "ALSA",
            GlobalDeviceIndex: 12,
            DeviceName: "Main speakers",
            ChannelCount: 2,
            SampleRate: 48000);
        var devices = new[]
        {
            new PortAudioOutputDeviceEntry(12, 0, "Main speakers", 1, 48000, false),
        };
        var hostApis = new[]
        {
            new PortAudioHostApiEntry(0, "ALSA", 0, 1, -1),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PortAudioOutputRuntime.ResolveCurrentOutputDevice(saved, devices, hostApis));
        Assert.Contains("supports 1 output channel", ex.Message);
        Assert.Contains("requested 2", ex.Message);
    }

    [Fact]
    public void GetClonesOf_ReturnsLocalVideoLinesWithMatchingCloneOfId()
    {
        var vm = new OutputManagementViewModel();
        var parentId = Guid.NewGuid();
        var cloneAId = Guid.NewGuid();
        var cloneBId = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new LocalVideoOutputDefinition(parentId, "Program", VideoOutputEngine.SdlOpenGl,
                VideoSurfaceMode.FullScreen, 0, null, null),
            new LocalVideoOutputDefinition(cloneAId, "Confidence", VideoOutputEngine.AvaloniaOpenGl,
                VideoSurfaceMode.Windowed, 0, 640, 360, CloneOfId: parentId),
            new LocalVideoOutputDefinition(cloneBId, "Wing", VideoOutputEngine.AvaloniaOpenGl,
                VideoSurfaceMode.Windowed, 1, 1280, 720, CloneOfId: parentId),
            new LocalVideoOutputDefinition(Guid.NewGuid(), "Other", VideoOutputEngine.SdlOpenGl,
                VideoSurfaceMode.Windowed, 0, 800, 600),
        });

        var clones = vm.GetClonesOf(parentId).ToList();
        Assert.Equal(2, clones.Count);
        Assert.Contains(clones, c => c.Definition.Id == cloneAId);
        Assert.Contains(clones, c => c.Definition.Id == cloneBId);
    }

    [Fact]
    public void GetPotentialCloneParents_ExcludesClonesAndExcludedLine()
    {
        var vm = new OutputManagementViewModel();
        var parentId = Guid.NewGuid();
        var cloneId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new LocalVideoOutputDefinition(parentId, "Program", VideoOutputEngine.SdlOpenGl,
                VideoSurfaceMode.FullScreen, 0, null, null),
            new LocalVideoOutputDefinition(cloneId, "Confidence", VideoOutputEngine.AvaloniaOpenGl,
                VideoSurfaceMode.Windowed, 0, 640, 360, CloneOfId: parentId),
            new LocalVideoOutputDefinition(otherId, "Other", VideoOutputEngine.SdlOpenGl,
                VideoSurfaceMode.Windowed, 0, 800, 600),
        });

        // Phase B caps clone chains at 1 deep — clones aren't themselves eligible parents.
        var candidates = vm.GetPotentialCloneParents().ToList();
        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.Id == cloneId);

        // Excluding "Other" from the candidate list (so a line being edited can't be its own parent).
        var otherLine = vm.Outputs.First(o => o.Definition.Id == otherId);
        var excluded = vm.GetPotentialCloneParents(otherLine).ToList();
        Assert.Single(excluded);
        Assert.Equal(parentId, excluded[0].Id);
    }

    /// <summary>UI rewrite P2: output aliases replace the virtual-audio-channel model. The alias is
    /// the displayed name everywhere; blank (or the original device name) clears it.</summary>
    [Fact]
    public void OutputAlias_EditAndClear_UpdatesEffectiveNameAndRaisesNamingChanged()
    {
        var vm = new OutputManagementViewModel();
        var paId = Guid.NewGuid();
        vm.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new PortAudioOutputDefinition(paId, "ALSA dev 1", 0, "Alsa", 1, "dev", 2, 48000),
        });
        var line = vm.Outputs[0];
        var namingChanges = 0;
        vm.OutputNamingChanged += (_, _) => namingChanges++;

        Assert.Equal("ALSA dev 1", line.EffectiveName);

        line.NameEdit = "  Main PA  ";
        Assert.Equal("Main PA", line.EffectiveName);
        Assert.Equal("Main PA", line.Definition.Alias);
        Assert.Equal("ALSA dev 1", line.Definition.DisplayName); // device name preserved
        Assert.Equal(1, namingChanges);

        line.NameEdit = "ALSA dev 1"; // committing the device name clears the alias
        Assert.Null(line.Definition.Alias);
        Assert.Equal("ALSA dev 1", line.EffectiveName);
        Assert.Equal(2, namingChanges);

        line.NameEdit = "   "; // blank also clears (no-op here: already null → no event)
        Assert.Null(line.Definition.Alias);
        Assert.Equal(2, namingChanges);
    }

    [Fact]
    public void OutputAlias_RoundTripsThroughProjectJson()
    {
        var def = new PortAudioOutputDefinition(Guid.NewGuid(), "dev", 0, "Alsa", 1, "dev", 2, 48000)
            { Alias = "Monitors" };
        var json = System.Text.Json.JsonSerializer.Serialize<OutputDefinition>(def);
        var back = System.Text.Json.JsonSerializer.Deserialize<OutputDefinition>(json);

        Assert.NotNull(back);
        Assert.Equal("Monitors", back.Alias);
        Assert.Equal("Monitors", back.EffectiveName);
    }

}

using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Save/load rework (2026-06-10): partial project files via <see cref="ProjectSections"/> —
/// scoped snapshot building and merge-on-apply semantics.</summary>
public sealed class ProjectSectionScopeTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ProjectSectionScopeTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    [Fact]
    public void Includes_NullMeansFullProject_ParentCoversChild()
    {
        Assert.True(ProjectSections.Includes(null, ProjectSections.OutputsAudio));
        Assert.True(ProjectSections.Includes([ProjectSections.Outputs], ProjectSections.OutputsAudio));
        Assert.True(ProjectSections.Includes([ProjectSections.OutputsVideo], ProjectSections.OutputsVideo));
        Assert.False(ProjectSections.Includes([ProjectSections.OutputsVideo], ProjectSections.OutputsAudio));
        Assert.False(ProjectSections.Includes([ProjectSections.CueLists], ProjectSections.Control));
    }

    [Fact]
    public void Normalize_CollapsesFullPairsToParents()
    {
        var normalized = ProjectSections.Normalize(
            [ProjectSections.OutputsAudio, ProjectSections.OutputsVideo, ProjectSections.TargetsMidi]);

        Assert.Contains(ProjectSections.Outputs, normalized);
        Assert.DoesNotContain(ProjectSections.OutputsAudio, normalized);
        Assert.Contains(ProjectSections.TargetsMidi, normalized);
    }

    [Fact]
    public void BuildProjectSnapshot_AudioOnly_CarriesOnlyAudioOutputs()
    {
        DispatchUi(static () =>
        {
            var vm = new MainViewModel();
            vm.OutputManagement.ReplaceDefinitionsForLoad(new OutputDefinition[]
            {
                new PortAudioOutputDefinition(Guid.NewGuid(), "PA", 0, "Alsa", 1, "dev", 2, 48000),
                new NDIOutputDefinition(Guid.NewGuid(), "NDI", "src", null, NDIOutputStreamMode.VideoAndAudio, 2, 48000),
            });

            var partial = vm.BuildProjectSnapshot([ProjectSections.OutputsAudio]);

            Assert.Equal([ProjectSections.OutputsAudio], partial.SavedSections);
            Assert.Single(partial.Outputs);
            Assert.IsType<PortAudioOutputDefinition>(partial.Outputs[0]);
            Assert.Empty(partial.CueLists);
            Assert.Empty(partial.Players);

            // Full snapshot stays unscoped for back-compat.
            Assert.Null(vm.BuildProjectSnapshot().SavedSections);
        });
    }

    [Fact]
    public void ApplyProjectSnapshot_PartialVideo_MergesAndKeepsAudioAndPlayers()
    {
        DispatchUi(static () =>
        {
            var vm = new MainViewModel();
            var paId = Guid.NewGuid();
            vm.OutputManagement.ReplaceDefinitionsForLoad(new OutputDefinition[]
            {
                new PortAudioOutputDefinition(paId, "PA", 0, "Alsa", 1, "dev", 2, 48000),
            });
            var playersBefore = vm.Players.Count;

            var incoming = new HaPlayProject
            {
                SavedSections = [ProjectSections.OutputsVideo],
                Outputs =
                {
                    new NDIOutputDefinition(Guid.NewGuid(), "New NDI", "src", null, NDIOutputStreamMode.VideoOnly, 2, 48000),
                },
            };
            vm.ApplyProjectSnapshot(incoming);

            // Audio line survived the video-only import; the new video line arrived;
            // the empty Players list in the partial file did NOT shrink the live players.
            Assert.Contains(vm.OutputManagement.Outputs, o => o.Definition.Id == paId);
            Assert.Contains(vm.OutputManagement.Outputs, o => o.Definition is NDIOutputDefinition);
            Assert.Equal(playersBefore, vm.Players.Count);
        });
    }
}

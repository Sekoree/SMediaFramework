using Avalonia.Headless;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>UX-10: an empty cue list shows a call-to-action, which clears the moment the first cue exists.</summary>
public sealed class CueEmptyStateTests
{
    [Fact]
    public void HasNoCues_TogglesWhenCuesAreAdded()
    {
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CueEmptyStateTests).Assembly)
            .Dispatch(static () =>
            {
                var vm = new CuePlayerViewModel();
                Assert.True(vm.HasNoCues); // fresh list, no cues

                var raised = false;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.HasNoCues))
                        raised = true;
                };

                vm.AddEmptyMediaCue();

                Assert.False(vm.HasNoCues); // a cue now exists
                Assert.True(raised);        // and the UI was notified to hide the CTA
            }, CancellationToken.None);
    }
}

using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class AppearanceSettingsTests
{
    // §8.6 / UI-01 follow-up: the Display-preferences section is now live because Simple/Fluent ship the
    // real dark + density resources the Classic-only limitation was waiting on. The base-theme selector is
    // always meaningful; the variant/density controls gate themselves per base theme.
    [Fact]
    public void AppearanceSettings_AreShown_WithAllThreeBaseThemes()
    {
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(AppearanceSettingsTests).Assembly)
            .Dispatch(static () =>
            {
                var vm = new MainViewModel();
                Assert.True(vm.ShowAppearanceSettings);
                Assert.Equal(
                    new[] { AppBaseTheme.Classic, AppBaseTheme.Simple, AppBaseTheme.Fluent },
                    vm.BaseThemeChoices);
            }, CancellationToken.None);
    }
}

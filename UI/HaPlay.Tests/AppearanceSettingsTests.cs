using Avalonia.Headless;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class AppearanceSettingsTests
{
    // UI-01: the Project workspace must NOT surface the theme/density controls while the Classic theme has
    // no working dark variant (Dark renders a broken half-dark window) and density is a hard no-op. The
    // Display-preferences section binds its visibility to ShowAppearanceSettings, so this guards against the
    // options silently reappearing before real dark + density resources exist.
    [Fact]
    public void AppearanceSettings_AreHidden_UntilThemeResourcesExist()
    {
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(AppearanceSettingsTests).Assembly)
            .Dispatch(static () =>
            {
                var vm = new MainViewModel();
                Assert.False(vm.ShowAppearanceSettings);
            }, CancellationToken.None);
    }
}

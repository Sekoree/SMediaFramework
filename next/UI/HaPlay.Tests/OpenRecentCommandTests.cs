using Avalonia.Headless;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class OpenRecentCommandTests
{
    // Regression: the recent-projects submenu binds OpenRecentCommand via an item Style that also matches
    // the submenu HEADER MenuItem, whose CommandParameter resolves to the MainViewModel (non-string). When
    // the command was string-typed, CanExecute cast that to string and threw InvalidCastException as the
    // File menu rendered — so the whole File menu couldn't open. The command takes object? and must tolerate
    // any parameter without throwing.
    [Fact]
    public void OpenRecentCommand_CanExecute_ToleratesAnyParameterType()
    {
        // MainViewModel's ctor applies the theme to Application.Current, which is owned by the
        // headless session's UI thread once any other test has started it — construct on that
        // thread like every other VM test, or this flakes order-dependently with VerifyAccess.
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(OpenRecentCommandTests).Assembly)
            .Dispatch(static () =>
            {
                var vm = new MainViewModel();

                Assert.True(vm.OpenRecentCommand.CanExecute(vm));            // the header case (non-string)
                Assert.True(vm.OpenRecentCommand.CanExecute(null));
                Assert.True(vm.OpenRecentCommand.CanExecute("/tmp/example.haplayproj"));
            }, CancellationToken.None);
    }
}

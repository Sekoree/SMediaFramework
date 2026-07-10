using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.OutputPreview;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Closing a local output window (the operator hitting its X) while a playback session drives it must be
/// VETOED, not honoured: the old behaviour disposed the sink out from under the composition pump - an
/// exception per submitted frame until the line teardown caught up - and silently killed a live show
/// output. Once playback releases the output, the X closes (and removes) the line exactly as before.
/// The SDL runtime applies the same guard in its CloseRequested handler; only the Avalonia engine's
/// window is constructible headless, so that path carries the regression coverage.
/// </summary>
public sealed class LocalOutputCloseVetoTests
{
    // Func<Task<int>> on purpose: the Func<Task> overload of Dispatch resolves to Dispatch<TResult>(Func<TResult>)
    // and returns without awaiting the async body.
    private static Task DispatchUi(Func<Task<int>> action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(LocalOutputCloseVetoTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    [Fact]
    public Task OperatorClose_WhilePlaybackHoldsTheOutput_IsVetoed_ThenHonoredOnceReleased() =>
        DispatchUi(async () =>
        {
            var outputs = new OutputManagementViewModel();
            var def = new LocalVideoOutputDefinition(
                Guid.NewGuid(), "Program", VideoOutputEngine.AvaloniaOpenGl, VideoSurfaceMode.Windowed, 0, 640, 360);
            outputs.ReplaceDefinitionsForLoad([def]);
            var line = outputs.Outputs[0];

            var runtime = new AvaloniaLocalVideoPreviewRuntime(def, line, outputs, screenReference: null);
            await runtime.StartAsync();

            // The window is deliberately private (no prod test hook); reflection keeps it that way.
            var window = (Avalonia.Controls.Window?)typeof(AvaloniaLocalVideoPreviewRuntime)
                .GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(runtime);
            Assert.NotNull(window);
            Assert.True(window!.IsVisible);

            var sink = runtime.AcquireForPlayback();
            Assert.NotNull(sink);

            // Operator X while a session drives the output: the close must be swallowed.
            window.Close();
            Assert.True(window.IsVisible, "window closed while a playback session was driving the output");
            Assert.Contains(line, outputs.Outputs);

            // Session lets go: the X now closes the window and removes the line (unchanged idle semantics).
            runtime.ReleaseFromPlayback();
            window.Close();
            Assert.False(window.IsVisible);
            Assert.DoesNotContain(line, outputs.Outputs);
            return 0;
        });
}

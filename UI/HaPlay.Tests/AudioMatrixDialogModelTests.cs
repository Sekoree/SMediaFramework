using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class AudioMatrixDialogModelTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(AudioMatrixDialogModelTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    [Fact]
    public void Matrix_outputs_and_rows_include_only_selected_audio_capable_outputs()
    {
        DispatchUi(static () =>
        {
            var outputs = new OutputManagementViewModel();
            outputs.ReplaceDefinitionsForLoad(new OutputDefinition[]
            {
                new LocalVideoOutputDefinition(Guid.NewGuid(), "Projector", VideoOutputEngine.SdlOpenGl,
                    VideoSurfaceMode.Windowed, 0, 1280, 720),
                new NDIOutputDefinition(Guid.NewGuid(), "Video only NDI", "video", null,
                    NDIOutputStreamMode.VideoOnly, 2, 48_000),
                new NDIOutputDefinition(Guid.NewGuid(), "Program NDI", "program", null,
                    NDIOutputStreamMode.VideoAndAudio, 4, 48_000),
                new NDIOutputDefinition(Guid.NewGuid(), "Audio NDI", "audio", null,
                    NDIOutputStreamMode.AudioOnly, 1, 48_000),
                new PortAudioOutputDefinition(Guid.NewGuid(), "PA", 0, "Alsa", 1, "dev", 2, 48_000),
            });
            var player = new MediaPlayerViewModel(outputs, "P1");

            foreach (var binding in player.Outputs)
                binding.IsSelected = true;

            var summaryNames = player.AudioMatrixOutputSummaries.Select(s => s.Name).ToHashSet();
            Assert.Equal(["Audio NDI", "PA", "Program NDI"], summaryNames.Order(StringComparer.Ordinal));
            Assert.DoesNotContain("Projector", summaryNames);
            Assert.DoesNotContain("Video only NDI", summaryNames);

            Assert.Equal(7, player.AudioMatrixRows.Count);
            Assert.All(player.AudioMatrixRows, row =>
            {
                Assert.DoesNotContain("Projector", row.Label);
                Assert.DoesNotContain("Video only NDI", row.Label);
            });
        });
    }
}

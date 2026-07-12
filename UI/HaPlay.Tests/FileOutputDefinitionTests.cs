using System.Text.Json;
using HaPlay.OutputPreview;
using HaPlay.ViewModels.Dialogs;
using S.Media.Encode.FFmpeg;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The record-to-file output line: persistence round-trip, settings→options mapping, dialog commit.</summary>
public sealed class FileOutputDefinitionTests
{
    [Fact]
    public void FileOutputDefinition_RoundTripsThroughProjectJson()
    {
        OutputDefinition def = new FileOutputDefinition(
            Guid.NewGuid(),
            "Show recording",
            "/tmp/recordings",
            "show_{timestamp}",
            new EncodeSettingsDefinition("Matroska", "VideoAndAudio", "H264", 0, 20, "fast", 0, 1280, 0)
            {
                AudioLegs =
                [
                    new EncodeAudioLegDefinition("Aac", 192_000, 2, 0, "Program", "eng"),
                    new EncodeAudioLegDefinition("Flac", 0, 1, 0, "Commentary", "jpn"),
                ],
            })
        { Alias = "Rec" };

        var json = JsonSerializer.Serialize(def);
        var back = JsonSerializer.Deserialize<OutputDefinition>(json);

        var file = Assert.IsType<FileOutputDefinition>(back);
        Assert.Equal(def.Id, file.Id);
        Assert.Equal("Rec", file.Alias);
        Assert.Equal("/tmp/recordings", file.DirectoryPath);
        Assert.Equal(2, file.EffectiveEncode.AudioLegs.Count);
        Assert.Equal("Commentary", file.EffectiveEncode.AudioLegs[1].Name);
        Assert.Equal(ManagedOutputKind.FileRecord, file.Kind);
    }

    [Fact]
    public void BuildOptions_MapsSettingsAndFallsBackOnUnknownNames()
    {
        var encode = new EncodeSettingsDefinition(
            "Matroska", "AudioOnly", "NotACodec", VideoBitrateBps: 0, VideoCrf: 23, "fast", 0, 0, 0)
        {
            AudioLegs = [new EncodeAudioLegDefinition("Opus", 128_000, 2, 44_100, "Main", "ger")],
        };

        var options = FileOutputRuntime.BuildOptions(encode);

        Assert.Equal(EncodeContainer.Matroska, options.Container);
        Assert.Equal(EncodeOutputMode.AudioOnly, options.OutputMode);
        Assert.Equal(EncodeVideoCodec.H264, options.Video.Codec); // unknown name → default
        var leg = Assert.Single(options.AudioLegs);
        Assert.Equal(EncodeAudioCodec.Opus, leg.Codec);
        Assert.Equal(44_100, leg.SampleRate);
        Assert.Equal("ger", leg.Language);
    }

    [Fact]
    public void DialogCommit_ProducesDefinition_AndFlagsBadFolder()
    {
        var vm = new AddFileOutputDialogViewModel
        {
            DisplayName = "Rec 1",
            DirectoryPath = Path.GetTempPath(),
            Container = "Matroska",
            VideoCodec = "Mpeg4", // always-present built-in encoder - commit's probe must pass anywhere
        };
        vm.AudioLegs[0].Codec = "Flac";
        vm.InitializeExistingOutputNames([]);

        var def = vm.TryCommit();

        Assert.NotNull(def);
        Assert.Equal("Rec 1", def.DisplayName);
        Assert.Equal("Matroska", def.EffectiveEncode.Container);

        vm.DirectoryPath = "";
        Assert.Null(vm.TryCommit());
        Assert.NotNull(vm.ValidationMessage);
    }

    [Fact]
    public void LiveStreamDefinition_RoundTripsThroughProjectJson_AndBuildsOptions()
    {
        OutputDefinition def = new LiveStreamOutputDefinition(
            Guid.NewGuid(),
            "Stage stream",
            new EncodeSettingsDefinition(Container: "MpegTs", VideoBitrateBps: 6_000_000, VideoCrf: null),
            new LocalStreamServerDefinition(Enabled: true, Port: 9100, EnableTs: true, EnableHls: false))
        {
            PushTargets = [new StreamPushTargetDefinition("Srt", "srt://encoder.local:9000")],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(def);
        var back = System.Text.Json.JsonSerializer.Deserialize<OutputDefinition>(json);

        var stream = Assert.IsType<LiveStreamOutputDefinition>(back);
        Assert.Equal(ManagedOutputKind.LiveStream, stream.Kind);
        Assert.Equal(9100, stream.EffectiveLocalServer.Port);
        var target = Assert.Single(stream.PushTargets);
        Assert.Equal("Srt", target.Protocol);

        var options = OutputPreview.LiveStreamOutputRuntime.BuildOptions(stream);
        Assert.Equal(S.Media.Stream.Http.PushProtocol.Srt, Assert.Single(options.PushTargets).Protocol);
        Assert.NotNull(options.LocalServer);
        Assert.True(options.LocalServer!.EnableTs);
        Assert.False(options.LocalServer.EnableHls);
        Assert.Equal(S.Media.Encode.FFmpeg.EncodeContainer.MpegTs, options.Encode.Container);
    }

    [Fact]
    public void Effects_PersistOnAnyOutputKind_AndBuildFromTheRegistry()
    {
        OutputDefinition def = new FileOutputDefinition(Guid.NewGuid(), "Rec", "/tmp")
        {
            Effects =
            [
                new OutputEffectDefinition("gain", OutputEffectTarget.Audio, "{\"gainDb\": -6}"),
                new OutputEffectDefinition("grayscale", OutputEffectTarget.Video),
                new OutputEffectDefinition("does-not-exist", OutputEffectTarget.Video),
            ],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(def);
        var back = System.Text.Json.JsonSerializer.Deserialize<OutputDefinition>(json)!;
        Assert.Equal(3, back.Effects.Count);
        Assert.Equal("gain", back.Effects[0].Kind);
        Assert.Equal("{\"gainDb\": -6}", back.Effects[0].ConfigJson);

        // Building resolves registry kinds and SKIPS unknown ones (old project files must not fault).
        var audio = ViewModels.OutputManagementViewModel.BuildAudioEffects(back);
        var video = ViewModels.OutputManagementViewModel.BuildVideoEffects(back);
        Assert.Single(audio);
        Assert.Single(video); // grayscale resolved, "does-not-exist" skipped
        var gain = Assert.IsType<S.Media.Routing.GainAudioEffect>(audio[0]);
        Assert.Equal(-6.0, gain.GainDb, 1);
        foreach (var e in audio) e.Dispose();
        foreach (var e in video) e.Dispose();
    }

    [Fact]
    public void GrayscaleEffect_ProducesGrayCopy_LeavingTheSharedInputUntouched()
    {
        var effect = new S.Media.Routing.GrayscaleVideoEffect();
        var format = new S.Media.Core.Video.VideoFormat(2, 1, S.Media.Core.Video.PixelFormat.Bgra32, new S.Media.Core.Video.Rational(30, 1));
        effect.Configure(format);

        var pixels = new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }; // blue px, red px (BGRA)
        var input = new S.Media.Core.Video.VideoFrame(TimeSpan.Zero, format, [pixels], [8]);

        var output = effect.Process(input, TimeSpan.Zero);
        var outPx = output.Planes[0].ToArray();
        Assert.Equal(outPx[0], outPx[1]); // gray: B == G == R
        Assert.Equal(outPx[1], outPx[2]);
        Assert.Equal(255, outPx[3]);      // alpha preserved
        Assert.Equal(255, pixels[0]);     // the shared input buffer was NOT mutated (fan-out safety)
        output.Dispose();
        effect.Dispose();
    }

    [Fact]
    public void ShowMapper_IncludeCanvas_DeclaresACompositionForAudioOnlyMedia()
    {
        // The visualizer path: audio-only media + VIZ ⇒ a canvas WITHOUT a video clip binding.
        var doc = Playback.MediaPlayerShowMapper.ToShowDocument(
            "song.flac", hasVideo: false, includeCanvas: true, canvasWidth: 1280, canvasHeight: 720);

        var composition = Assert.Single(doc.Compositions);
        Assert.Equal(1280, composition.Width);
        Assert.Null(Assert.Single(doc.Clips).CompositionId); // the audio clip does NOT bind the canvas

        // Plain audio-only open keeps skipping the canvas entirely.
        var plain = Playback.MediaPlayerShowMapper.ToShowDocument("song.flac", hasVideo: false);
        Assert.Empty(plain.Compositions);
    }

    [Fact]
    public void DialogCommit_RejectsProResInMp4()
    {
        var vm = new AddFileOutputDialogViewModel
        {
            DisplayName = "Rec bad",
            DirectoryPath = Path.GetTempPath(),
            Container = "Mp4",
            VideoCodec = "ProRes422",
        };
        vm.InitializeExistingOutputNames([]);

        Assert.Null(vm.TryCommit());
        Assert.Contains("ProRes", vm.ValidationMessage);
    }
}

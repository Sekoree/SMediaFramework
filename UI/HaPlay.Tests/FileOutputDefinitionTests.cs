using System.Text.Json;
using HaPlay.OutputPreview;
using HaPlay.ViewModels.Dialogs;
using S.Media.Decode.FFmpeg;
using S.Media.Decode.FFmpeg.Video;
using S.Media.Encode.FFmpeg;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The record-to-file output line: persistence round-trip, settings→options mapping, dialog commit.</summary>
public sealed class FileOutputDefinitionTests
{
    /// <summary>True when this machine can actually open the given encoders. Mirrors the
    /// <c>Validate().Count &gt; 0</c> skip guard the encode round-trip tests use, so dialog-commit tests
    /// that assert on a produced definition skip (rather than fail) on a runner without usable FFmpeg
    /// natives instead of crashing.</summary>
    private static bool EncodersUsable(string videoCodec, string audioCodec = "Flac") =>
        FileOutputRuntime.BuildOptions(new EncodeSettingsDefinition(
                Container: "Matroska",
                OutputMode: "VideoAndAudio",
                VideoCodec: videoCodec,
                VideoCrf: null,
                ScaleWidth: 1920,
                ScaleHeight: 1080,
                Fps: 30)
            {
                AudioLegs = [new EncodeAudioLegDefinition(audioCodec, Channels: 2)],
            }).Validate().Count == 0;

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
        {
            Alias = "Rec",
            RecordingMode = FileOutputDefinition.ContinuousProgramRecordingMode,
        };

        var json = JsonSerializer.Serialize(def);
        var back = JsonSerializer.Deserialize<OutputDefinition>(json);

        var file = Assert.IsType<FileOutputDefinition>(back);
        Assert.Equal(def.Id, file.Id);
        Assert.Equal("Rec", file.Alias);
        Assert.Equal("/tmp/recordings", file.DirectoryPath);
        Assert.Equal(2, file.EffectiveEncode.AudioLegs.Count);
        Assert.Equal("Commentary", file.EffectiveEncode.AudioLegs[1].Name);
        Assert.True(file.RecordsContinuousProgram);
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
        if (!EncodersUsable("Mpeg4"))
            return; // no usable FFmpeg on this runner - the commit probe cannot pass

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
        Assert.Equal(FileOutputDefinition.ContinuousProgramRecordingMode, def.EffectiveRecordingMode);
        Assert.Equal(1920, def.EffectiveEncode.ScaleWidth);
        Assert.Equal(1080, def.EffectiveEncode.ScaleHeight);
        Assert.Equal(30, def.EffectiveEncode.Fps);

        vm.DirectoryPath = "";
        Assert.Null(vm.TryCommit());
        Assert.NotNull(vm.ValidationMessage);
    }

    [Fact]
    public void LegacyFileOutput_RemainsContentOnly_AndDialogCanSelectEitherPolicy()
    {
        // RecordingMode did not exist in older project JSON. Preserve its gap-collapsing behavior
        // rather than silently changing file duration or requiring a fixed raster on project load.
        var legacy = new FileOutputDefinition(Guid.NewGuid(), "Legacy", "/tmp");
        Assert.Equal(FileOutputDefinition.ContentOnlyRecordingMode, legacy.EffectiveRecordingMode);
        Assert.False(legacy.RecordsContinuousProgram);

        var vm = new AddFileOutputDialogViewModel
        {
            DisplayName = "Content record",
            DirectoryPath = Path.GetTempPath(),
            Container = "Matroska",
            VideoCodec = "Mpeg4",
            RecordingMode = FileOutputDefinition.ContentOnlyRecordingMode,
            ScaleWidth = 0,
            ScaleHeight = 0,
            Fps = 0,
        };
        vm.InitializeExistingOutputNames([]);
        if (!EncodersUsable("Mpeg4"))
            return; // no usable FFmpeg on this runner - the commit probe cannot pass

        var contentOnly = vm.TryCommit();
        Assert.NotNull(contentOnly);
        Assert.False(contentOnly.RecordsContinuousProgram);

        vm.RecordingMode = FileOutputDefinition.ContinuousProgramRecordingMode;
        Assert.Null(vm.TryCommit());
        Assert.Equal(Resources.Strings.FileOutputContinuousFormatRequired, vm.ValidationMessage);
    }

    [Fact]
    public async Task ContinuousFileRecording_WritesBlackAndSilenceWhileIdle()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"haplay-continuous-record-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var encode = new EncodeSettingsDefinition(
                Container: "Matroska",
                OutputMode: "VideoAndAudio",
                VideoCodec: "Mpeg4",
                VideoCrf: null,
                ScaleWidth: 128,
                ScaleHeight: 96,
                Fps: 30)
            {
                AudioLegs = [new EncodeAudioLegDefinition("Flac", Channels: 2)],
            };
            var definition = new FileOutputDefinition(
                Guid.NewGuid(), "Continuous", directory, "continuous", encode)
            {
                RecordingMode = FileOutputDefinition.ContinuousProgramRecordingMode,
            };
            if (FileOutputRuntime.BuildOptions(encode).Validate().Count > 0)
                return;

            string path;
            using (var runtime = new FileOutputRuntime(definition))
            {
                runtime.Arm();
                path = runtime.CurrentFilePath!;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                while ((runtime.GetMetrics()?.VideoFramesEncoded ?? 0) < 10 && DateTime.UtcNow < deadline)
                    await Task.Delay(20);

                var metrics = runtime.GetMetrics()!;
                Assert.True(metrics.VideoFramesEncoded >= 10,
                    "continuous recording did not encode idle black frames");
                Assert.True(metrics.Sinks[0].BytesWritten > 0,
                    "continuous recording did not mux its idle program");
                await runtime.DisarmAsync();
            }

            Assert.True(new FileInfo(path).Length > 256);
            using var decoder = MediaContainerDecoder.Open(
                path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            Assert.True(decoder.HasVideo);
            Assert.True(decoder.HasAudio);
            Assert.Equal(128, decoder.Video.Format.Width);
            Assert.Equal(96, decoder.Video.Format.Height);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ContentOnlyFileRecording_DoesNotInventIdleFrames()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"haplay-content-record-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var encode = new EncodeSettingsDefinition(
                Container: "Matroska",
                OutputMode: "VideoOnly",
                VideoCodec: "Mpeg4",
                VideoCrf: null,
                ScaleWidth: 128,
                ScaleHeight: 96,
                Fps: 0);
            var definition = new FileOutputDefinition(Guid.NewGuid(), "Content", directory, "content", encode)
            {
                RecordingMode = FileOutputDefinition.ContentOnlyRecordingMode,
            };
            if (FileOutputRuntime.BuildOptions(encode).Validate().Count > 0)
                return;

            using var runtime = new FileOutputRuntime(definition);
            runtime.Arm();
            await Task.Delay(250);
            Assert.Equal(0, runtime.GetMetrics()!.VideoFramesSubmitted);

            // Once configured, one source frame is content; repeated presentations with the same
            // stopped timestamp are held-canvas output and must not lengthen video without audio.
            var output = runtime.AcquireForPlayback(needsVideo: true, needsAudio: false).Video!;
            var format = new S.Media.Core.Video.VideoFormat(
                128, 96, S.Media.Core.Video.PixelFormat.Bgra32, new S.Media.Core.Video.Rational(30, 1));
            output.Configure(format);
            for (var i = 0; i < 12; i++)
                output.Submit(new S.Media.Core.Video.VideoFrame(
                    TimeSpan.Zero, format, [new byte[128 * 96 * 4]], [128 * 4]));
            await Task.Delay(100);
            Assert.Equal(1, runtime.GetMetrics()!.VideoFramesSubmitted);

            // A genuinely advancing source timestamp resumes content capture immediately.
            output.Submit(new S.Media.Core.Video.VideoFrame(
                TimeSpan.FromMilliseconds(33), format, [new byte[128 * 96 * 4]], [128 * 4]));
            await Task.Delay(100);
            Assert.Equal(2, runtime.GetMetrics()!.VideoFramesSubmitted);
            await runtime.DisarmAsync();
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void LiveStreamDefinition_RoundTripsThroughProjectJson_AndBuildsOptions()
    {
        OutputDefinition def = new LiveStreamOutputDefinition(
            Guid.NewGuid(),
            "Stage stream",
            new EncodeSettingsDefinition(Container: "MpegTs", VideoBitrateBps: 6_000_000, VideoCrf: null)
            {
                VideoBitrateMode = "Constant",
                VideoMaxBFrames = 0,
                VideoVbvBufferMilliseconds = 500,
                VideoLowLatencyTune = true,
            },
            new LocalStreamServerDefinition(Enabled: true, Port: 9100, EnableTs: true, EnableHls: false))
        {
            PushTargets =
            [
                new StreamPushTargetDefinition("Srt", "srt://encoder.local:9000")
                {
                    SrtLatencyMilliseconds = 120,
                },
            ],
        };

        var json = System.Text.Json.JsonSerializer.Serialize(def);
        var back = System.Text.Json.JsonSerializer.Deserialize<OutputDefinition>(json);

        var stream = Assert.IsType<LiveStreamOutputDefinition>(back);
        Assert.Equal(ManagedOutputKind.LiveStream, stream.Kind);
        Assert.Equal(9100, stream.EffectiveLocalServer.Port);
        var target = Assert.Single(stream.PushTargets);
        Assert.Equal("Srt", target.Protocol);
        Assert.Equal(120, target.SrtLatencyMilliseconds);

        var options = OutputPreview.LiveStreamOutputRuntime.BuildOptions(stream);
        Assert.Equal(S.Media.Stream.Http.PushProtocol.Srt, Assert.Single(options.PushTargets).Protocol);
        Assert.NotNull(options.LocalServer);
        Assert.True(options.LocalServer!.EnableTs);
        Assert.False(options.LocalServer.EnableHls);
        Assert.Equal(S.Media.Encode.FFmpeg.EncodeContainer.MpegTs, options.Encode.Container);
        Assert.Equal(EncodeVideoBitrateMode.Constant, options.Encode.Video.BitrateMode);
        Assert.Equal(3_000_000, options.Encode.Video.BufferSizeBits);
        Assert.Equal(0, options.Encode.Video.MaxBFrames);
        Assert.True(options.Encode.Video.LowLatencyTune);
        Assert.Equal(120, Assert.Single(options.PushTargets).SrtLatencyMilliseconds);
    }

    [Fact]
    public void LiveStreamDialog_LowLatencyPreset_CommitsInspectableAdvancedSettings()
    {
        if (!EncodersUsable("H264"))
            return; // no usable FFmpeg on this runner - the commit probe cannot pass

        var vm = new AddLiveStreamOutputDialogViewModel
        {
            DisplayName = "Low latency",
            VideoCodec = "H264",
        };
        vm.InitializeExistingOutputNames([]);
        vm.PushTargets.Add(new StreamPushTargetRowViewModel
        {
            Protocol = "Srt",
            Url = "srt://encoder.local:9000",
            SrtLatencyMilliseconds = 180,
        });

        vm.ApplyLowLatencyPresetCommand.Execute(null);
        var definition = vm.TryCommit();

        Assert.NotNull(definition);
        Assert.Equal("Constant", definition.EffectiveEncode.VideoBitrateMode);
        Assert.Equal(definition.EffectiveEncode.Fps, definition.EffectiveEncode.GopSize);
        Assert.Equal(0, definition.EffectiveEncode.VideoMaxBFrames);
        Assert.Equal(500, definition.EffectiveEncode.VideoVbvBufferMilliseconds);
        Assert.True(definition.EffectiveEncode.VideoLowLatencyTune);
        Assert.Equal(180, Assert.Single(definition.PushTargets).SrtLatencyMilliseconds);
    }

    [Fact]
    public void LiveStreamRuntime_RetainsStartupFailureForHealthPolling()
    {
        var definition = new LiveStreamOutputDefinition(
            Guid.NewGuid(),
            "No destination",
            new EncodeSettingsDefinition(
                Container: "MpegTs",
                OutputMode: "VideoOnly",
                VideoCodec: "H264",
                VideoCrf: 30,
                ScaleWidth: 128,
                ScaleHeight: 96,
                Fps: 30),
            new LocalStreamServerDefinition(Enabled: false));
        using var runtime = new OutputPreview.LiveStreamOutputRuntime(definition);

        Assert.Throws<ArgumentException>(() => runtime.GoLive());

        var state = runtime.GetRuntimeState();
        Assert.False(state.IsLive);
        Assert.Null(state.Status);
        Assert.Contains("no destination", state.LastError, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task RecordingPathReservation_IsAtomicAndUniqueAcrossConcurrentLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"haplay-recording-{Guid.NewGuid():N}");
        try
        {
            var definition = new FileOutputDefinition(
                Guid.NewGuid(), "Recording", directory, "same-name", new EncodeSettingsDefinition("Matroska"));
            var paths = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
                FileOutputRuntime.ReserveUniqueFilePath(definition, EncodeContainer.Matroska))));

            Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
            Assert.All(paths, path =>
            {
                Assert.True(File.Exists(path));
                Assert.Equal(directory, Path.GetDirectoryName(path));
                Assert.EndsWith(".mkv", path, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* best effort */ }
        }
    }
}

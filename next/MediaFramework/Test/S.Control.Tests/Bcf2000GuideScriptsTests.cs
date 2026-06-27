using System.IO;
using System.Text.RegularExpressions;
using S.Control;
using Xunit;
using Xunit.Abstractions;

namespace HaPlay.Tests;

// Compiles the Mond scripts embedded in Doc/HaPlay-Control-X32-BCF2000-Layers.md so the guide
// can't ship code that fails to compile. Reads the live doc (no copy drift) and asserts the
// runtime reports no Compile-stage diagnostics. Skips if the repo layout isn't reachable.
public sealed class Bcf2000GuideScriptsTests
{
    private readonly ITestOutputHelper _o;
    public Bcf2000GuideScriptsTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void GuideScripts_Compile()
    {
        var docPath = FindRepoFile("Doc/HaPlay-Control-X32-BCF2000-Layers.md");
        if (docPath is null)
        {
            _o.WriteLine("Guide doc not found from test base dir; skipping.");
            return;
        }

        var markdown = File.ReadAllText(docPath);
        var blocks = Regex.Matches(markdown, "```js\\r?\\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Step 3 helper, Step 4 bank_1, Step 5 outputs, Step 6 layers (in document order).
        Assert.True(blocks.Count >= 4, $"expected at least 4 ```js blocks, found {blocks.Count}");

        var sources = new Dictionary<string, string>
        {
            ["Scripts/bcf_surface.mnd"] = blocks[0],
            ["Scripts/bcf_bank_1.mnd"] = blocks[1],
            ["Scripts/bcf_outputs.mnd"] = blocks[2],
            ["Scripts/bcf_layers.mnd"] = blocks[3],
        };

        var deviceId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = deviceId,
                    Name = "BCF2000",
                    ProfileId = "behringer.bcf2000",
                    Protocol = ControlDeviceProtocol.Midi,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig { Alias = "bcf" },
                },
                new ControlDeviceInstanceConfig
                {
                    Id = Guid.NewGuid(),
                    Name = "X32",
                    ProfileId = "behringer.x32.osc",
                    Protocol = ControlDeviceProtocol.Osc,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig { Alias = "x32", OscHost = "127.0.0.1", OscPort = 10023 },
                },
            ],
            Scripts =
            [
                NoTriggerScript("Scripts/bcf_surface.mnd"),
                ProjectScript("Scripts/bcf_bank_1.mnd", deviceId, "onNote"),
                ProjectScript("Scripts/bcf_outputs.mnd", deviceId, "onNote"),
                ProjectScript("Scripts/bcf_layers.mnd", deviceId, "onLayerNav"),
            ],
        };

        var runtime = new ControlScriptRuntime(
            config,
            new InMemoryControlScriptSourceProvider(sources),
            new ControlScriptRuntimeServices(NullControlScriptCommandSink.Instance, new ControlValueCache()));

        // A MidiControlChange matches none of the MidiNote triggers, so no handler body runs — but
        // arming + dispatch makes the runtime compile every configured script (and its required helper).
        runtime.DispatchControlEvent(new MidiControlEvent(
            DateTimeOffset.UtcNow, deviceId, deviceId, Guid.NewGuid(),
            Channel: 1, Controller: 99, Value: 0, HighResolution14Bit: false));

        var compileErrors = runtime.Diagnostics
            .Where(d => d.Stage == ControlScriptDiagnosticStage.Compile)
            .ToList();

        Assert.True(
            compileErrors.Count == 0,
            "Guide scripts produced compile diagnostics:\n" + string.Join("\n", compileErrors.Select(d => $"  - {d.Message}")));
    }

    [Fact]
    public void GuideLayerNav_PressingNextLayerRunsWithoutRuntimeError()
    {
        var docPath = FindRepoFile("Doc/HaPlay-Control-X32-BCF2000-Layers.md");
        if (docPath is null)
        {
            _o.WriteLine("Guide doc not found from test base dir; skipping.");
            return;
        }

        var blocks = Regex.Matches(File.ReadAllText(docPath), "```js\\r?\\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(blocks.Count >= 4, $"expected at least 4 ```js blocks, found {blocks.Count}");

        var deviceId = Guid.NewGuid();
        var names = new[] { "Channels 1-8", "Channels 9-16", "Channels 17-24", "Channels 25-32", "Outputs" };
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Layers = [.. names.Select((n, i) => new ControlLayerConfig { Id = Guid.NewGuid(), Name = n, IsEnabled = i == 0, Priority = i })],
            Devices =
            [
                new ControlDeviceInstanceConfig
                {
                    Id = deviceId,
                    Name = "BCF2000",
                    Protocol = ControlDeviceProtocol.Midi,
                    IsEnabled = true,
                    Binding = new ControlDeviceBindingConfig { Alias = "bcf" },
                },
            ],
            Scripts =
            [
                new ControlScriptConfig
                {
                    Id = Guid.NewGuid(),
                    Name = "Layers",
                    ScriptPath = "Scripts/bcf_layers.mnd",
                    Scope = ControlScriptScope.Project,
                    Triggers =
                    [
                        new ControlScriptTriggerConfig
                        {
                            Kind = ControlScriptTriggerKind.MidiNote,
                            FunctionName = "onLayerNav",
                            DeviceInstanceId = deviceId,
                            MidiNote = 51,
                        },
                    ],
                },
            ],
        };

        var runtime = new ControlScriptRuntime(
            config,
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string> { ["Scripts/bcf_layers.mnd"] = blocks[3] }),
            new ControlScriptRuntimeServices(NullControlScriptCommandSink.Instance, new ControlValueCache()));

        // Press the "next layer" button (note 51) -> onLayerNav runs. It calls layerNames.length(); if that
        // array were named UPPERCASE, Mond wouldn't pass it as `this` and this would throw at runtime with
        // "Array.length: missing instance argument". (Compile-only checks don't catch that.)
        var result = runtime.DispatchControlEvent(new MidiNoteControlEvent(
            DateTimeOffset.UtcNow, deviceId, deviceId, Guid.NewGuid(),
            Channel: 1, Note: 51, Velocity: 127, IsNoteOn: true));

        Assert.NotEmpty(result.Invocations); // the trigger actually fired
        var runtimeErrors = runtime.Diagnostics
            .Where(d => d.Stage == ControlScriptDiagnosticStage.Runtime)
            .ToList();
        Assert.True(
            runtimeErrors.Count == 0,
            "Layer-nav produced runtime errors:\n" + string.Join("\n", runtimeErrors.Select(d => $"  - {d.Message}")));
    }

    private static ControlScriptConfig NoTriggerScript(string path) =>
        new() { Id = Guid.NewGuid(), Name = path, ScriptPath = path, Scope = ControlScriptScope.Project };

    private static ControlScriptConfig ProjectScript(string path, Guid deviceId, string function) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = path,
            ScriptPath = path,
            Scope = ControlScriptScope.Project,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.MidiNote,
                    FunctionName = function,
                    DeviceInstanceId = deviceId,
                    MidiNote = 1,
                },
            ],
        };

    private static string? FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}

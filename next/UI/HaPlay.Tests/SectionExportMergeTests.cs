using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Save/load rework — the two remaining granular formats: composition sets (name-keyed
/// merge into a cue list) and control-layer slices (extract + replace-by-name merge).</summary>
public sealed class SectionExportMergeTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(SectionExportMergeTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    [Fact]
    public void MergeCompositions_UpdatesByNameKeepingId_AddsNewNames()
    {
        DispatchUi(static () =>
        {
            var vm = new CuePlayerViewModel();
            Assert.NotNull(vm.SelectedCueList);
            var existing = CueCompositionViewModel.FromModel(
                new CueComposition { Name = "Main", Width = 1280, Height = 720 });
            vm.VisibleCompositions.Add(existing);
            var keepId = existing.Id;

            var (updated, added) = vm.MergeCompositions(
            [
                new CueComposition { Name = "main", Width = 1920, Height = 1080 }, // case-insensitive update
                new CueComposition { Name = "Overlay", Width = 1920, Height = 1080 },
            ]);

            Assert.Equal(1, updated);
            Assert.Equal(1, added);
            Assert.Equal(2, vm.VisibleCompositions.Count);
            Assert.Equal(keepId, vm.VisibleCompositions[0].Id); // placements stay bound
            Assert.Equal(1920, vm.VisibleCompositions[0].Width);
            Assert.Equal("Overlay", vm.VisibleCompositions[1].Name);
        });
    }

    [Fact]
    public void ControlLayerSlice_ExtractCarriesLayerAndItsScriptsOnly()
    {
        var layerA = new ControlLayerConfig { Name = "Show A" };
        var scriptA = new ControlScriptConfig { Name = "faders", Scope = ControlScriptScope.Layer, LayerId = layerA.Id };
        layerA.ScriptIds.Add(scriptA.Id);
        var unrelated = new ControlScriptConfig { Name = "project script" };
        var config = new ControlSystemConfig
        {
            Layers = { layerA, new ControlLayerConfig { Name = "Show B" } },
            Scripts = { scriptA, unrelated },
        };

        var slice = ControlConfigSlices.ExtractLayers(config, [layerA.Id]);

        Assert.Single(slice.Layers);
        Assert.Equal("Show A", slice.Layers[0].Name);
        Assert.Single(slice.Scripts);
        Assert.Equal("faders", slice.Scripts[0].Name);
        Assert.Empty(slice.Devices); // a slice describes the layer, not the rig
    }

    [Fact]
    public void ControlLayerSlice_MergeReplacesByName_AndRegeneratesCollidingScriptIds()
    {
        // Target: an old "Show A" layer with one script, plus an unrelated script whose id will
        // collide with the incoming slice's script id.
        var oldLayer = new ControlLayerConfig { Name = "Show A" };
        var oldScript = new ControlScriptConfig { Scope = ControlScriptScope.Layer, LayerId = oldLayer.Id };
        oldLayer.ScriptIds.Add(oldScript.Id);
        var collidingId = Guid.NewGuid();
        var unrelated = new ControlScriptConfig { Id = collidingId, Name = "keep me" };
        var target = new ControlSystemConfig { Layers = { oldLayer }, Scripts = { oldScript, unrelated } };

        var newLayer = new ControlLayerConfig { Name = "show a" }; // case-insensitive replace
        var newScript = new ControlScriptConfig { Id = collidingId, Scope = ControlScriptScope.Layer, LayerId = newLayer.Id };
        var newLayerWithScript = newLayer with { ScriptIds = [newScript.Id] };
        var slice = new ControlSystemConfig { Layers = { newLayerWithScript }, Scripts = { newScript } };

        var merged = ControlConfigSlices.MergeLayers(target, slice);

        var mergedLayer = Assert.Single(merged.Layers);
        Assert.Equal("show a", mergedLayer.Name);
        Assert.Equal(2, merged.Scripts.Count); // unrelated + the imported layer script
        Assert.Contains(merged.Scripts, s => s.Name == "keep me" && s.Id == collidingId);

        // The imported script's colliding id was regenerated and stays consistent with the layer.
        var imported = Assert.Single(merged.Scripts, s => s.Name != "keep me");
        Assert.NotEqual(collidingId, imported.Id);
        Assert.Equal(imported.Id, Assert.Single(mergedLayer.ScriptIds));
        Assert.Equal(mergedLayer.Id, imported.LayerId);
    }
}

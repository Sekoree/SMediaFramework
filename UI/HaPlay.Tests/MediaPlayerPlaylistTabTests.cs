using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Per-Set (playlist tab) commands: the rename-mode X / context-menu Remove
/// (<c>RemovePlaylistTabItem</c>) and the context-menu Duplicate (<c>DuplicatePlaylistTab</c>).</summary>
public sealed class MediaPlayerPlaylistTabTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(MediaPlayerPlaylistTabTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    private static MediaPlayerViewModel CreatePlayer() => new(new OutputManagementViewModel(), "P1");

    [Theory]
    [InlineData("Set 1", "Set 1 copy")]
    [InlineData("  Live  ", "Live copy")]
    [InlineData("", "Set copy")]
    [InlineData(null, "Set copy")]
    public void BuildDuplicateTabName_AppendsCopy(string? input, string expected) =>
        Assert.Equal(expected, MediaPlayerViewModel.BuildDuplicateTabName(input));

    [Fact]
    public void RemovePlaylistTabItem_RemovesTheGivenSet_NotJustTheSelected()
    {
        DispatchUi(() =>
        {
            var vm = CreatePlayer();
            vm.AddPlaylistTabCommand.Execute(null); // 2 Sets; the new one is selected
            var first = vm.PlaylistTabs[0];
            var second = vm.PlaylistTabs[1];
            Assert.Same(second, vm.SelectedPlaylistTab);

            vm.RemovePlaylistTabItemCommand.Execute(first); // remove the non-selected first Set

            Assert.Same(second, Assert.Single(vm.PlaylistTabs));
            Assert.Same(second, vm.SelectedPlaylistTab);
        });
    }

    [Fact]
    public void RemovePlaylistTabItem_NeverRemovesTheLastSet()
    {
        DispatchUi(() =>
        {
            var vm = CreatePlayer();
            var only = Assert.Single(vm.PlaylistTabs);

            vm.RemovePlaylistTabItemCommand.Execute(only);

            Assert.Same(only, Assert.Single(vm.PlaylistTabs));
        });
    }

    [Fact]
    public void DuplicatePlaylistTab_ClonesItemsWithFreshIds_InsertsAfter_AndSelectsCopy()
    {
        DispatchUi(() =>
        {
            var vm = CreatePlayer();
            var source = vm.PlaylistTabs[0];
            source.Name = "Set A";
            source.AutoAdvance = true;
            var item = new FilePlaylistItem("/m/clip.mp4");
            source.Items.Add(item);

            vm.DuplicatePlaylistTabCommand.Execute(source);

            Assert.Equal(2, vm.PlaylistTabs.Count);
            var copy = vm.PlaylistTabs[1]; // inserted right after the original
            Assert.Same(copy, vm.SelectedPlaylistTab);
            Assert.Equal("Set A copy", copy.Name);
            Assert.True(copy.AutoAdvance);

            var copiedItem = Assert.Single(copy.Items);
            Assert.Equal(item.Path, ((FilePlaylistItem)copiedItem).Path); // same content...
            Assert.NotEqual(item.Id, copiedItem.Id);                      // ...fresh id
            Assert.Single(source.Items); // original untouched
        });
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HaPlay.Models;
using S.Media.Session;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// D10: a full project save publishes one framework <see cref="ShowDocument"/> per cue list next to
/// the project file - the headless / C-ABI-runnable artifact of the saved show.
/// </summary>
public sealed class ShowDocumentSidecarTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("haplay-sidecar-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private string ProjectPath => Path.Combine(_dir, "myshow.haplay");

    private static CueList MediaCueList(string name) => new()
    {
        Name = name,
        Nodes =
        {
            new MediaCueNode
            {
                Number = "1",
                Label = "Track",
                Source = new FilePlaylistItem("/tmp/track.wav"),
            },
        },
    };

    [Fact]
    public async Task WriteAll_ProducesOneValidatedHeadlessDocumentPerCueList()
    {
        var project = new HaPlayProject
        {
            CueLists = { MediaCueList("Act 1"), MediaCueList("Act 2") },
        };

        var errors = new List<string>();
        var written = await ShowDocumentSidecar.WriteAllAsync(project, ProjectPath, errors);

        Assert.Empty(errors);
        Assert.Equal(
            [ShowDocumentSidecar.PathFor(ProjectPath, 1), ShowDocumentSidecar.PathFor(ProjectPath, 2)],
            written);

        foreach (var path in written)
        {
            // The sidecar must round-trip through the exact gate a headless host applies at load.
            var document = ShowDocument.FromJson(await File.ReadAllTextAsync(path));
            ShowDocumentValidator.ThrowIfInvalid(document);
            Assert.Single(document.Cues);
            Assert.Single(document.Clips);
        }
    }

    [Fact]
    public async Task WriteAll_RemovesStaleSidecars_WhenTheListCountShrinks()
    {
        var errors = new List<string>();
        await ShowDocumentSidecar.WriteAllAsync(
            new HaPlayProject { CueLists = { MediaCueList("Act 1"), MediaCueList("Act 2") } },
            ProjectPath, errors);
        Assert.True(File.Exists(ShowDocumentSidecar.PathFor(ProjectPath, 2)));

        await ShowDocumentSidecar.WriteAllAsync(
            new HaPlayProject { CueLists = { MediaCueList("Act 1") } },
            ProjectPath, errors);

        Assert.Empty(errors);
        Assert.True(File.Exists(ShowDocumentSidecar.PathFor(ProjectPath, 1)));
        Assert.False(File.Exists(ShowDocumentSidecar.PathFor(ProjectPath, 2)), "stale sidecar should be deleted");
    }

    [Fact]
    public async Task WriteAll_EmptyCueList_StillWritesALoadableEmptyShow()
    {
        var errors = new List<string>();
        var written = await ShowDocumentSidecar.WriteAllAsync(
            new HaPlayProject { CueLists = { new CueList { Name = "Empty" } } },
            ProjectPath, errors);

        Assert.Empty(errors);
        var document = ShowDocument.FromJson(await File.ReadAllTextAsync(Assert.Single(written)));
        ShowDocumentValidator.ThrowIfInvalid(document);
        Assert.Empty(document.Cues);
    }
}

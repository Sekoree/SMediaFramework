using HaPlay.Models;
using HaPlay.Services;
using Xunit;

namespace HaPlay.Tests;

public sealed class ProjectPersistenceCoordinatorTests
{
    [Fact]
    public async Task PersistAsync_SupersedesQueuedOlderSnapshot_AndWritesNewestLast()
    {
        var enteredBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<string?>();
        var coordinator = new ProjectPersistenceCoordinator(async (project, _, _) =>
        {
            if (project.HaPlayVersion == "blocker")
            {
                enteredBlocker.SetResult();
                await releaseBlocker.Task;
            }
            lock (writes)
                writes.Add(project.HaPlayVersion);
        });

        var blocker = coordinator.PersistAsync(new HaPlayProject { HaPlayVersion = "blocker" }, "/tmp/show.haplayproj");
        await enteredBlocker.Task;
        var stale = coordinator.PersistAsync(new HaPlayProject { HaPlayVersion = "stale" }, "/tmp/show.haplayproj");
        var newest = coordinator.PersistAsync(new HaPlayProject { HaPlayVersion = "newest" }, "/tmp/show.haplayproj");
        releaseBlocker.SetResult();

        await blocker;
        var staleResult = await stale;
        var newestResult = await newest;

        Assert.True(staleResult.WasSuperseded);
        Assert.True(newestResult.Succeeded);
        Assert.Equal(["blocker", "newest"], writes);
        Assert.True(coordinator.IsPersisted("/tmp/show.haplayproj", ProjectHash.Of(new HaPlayProject { HaPlayVersion = "newest" })));
    }

    [Fact]
    public async Task PersistAsync_DoesNotMarkGenerationPersisted_WhenPublishingFails()
    {
        var coordinator = new ProjectPersistenceCoordinator((_, _, _) => Task.CompletedTask);
        var project = new HaPlayProject { HaPlayVersion = "x" };
        var result = await coordinator.PersistAsync(
            project,
            "/tmp/show.haplayproj",
            (_, _, _) => Task.FromResult(new ProjectPublishResult([], ["sidecar failed"])));

        Assert.True(result.Succeeded);
        Assert.False(coordinator.IsPersisted("/tmp/show.haplayproj", ProjectHash.Of(project)));
    }
}

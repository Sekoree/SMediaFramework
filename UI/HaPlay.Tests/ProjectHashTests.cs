using HaPlay.Models;
using Xunit;

namespace HaPlay.Tests;

public sealed class ProjectHashTests
{
    [Fact]
    public void SerializedJsonOverloadMatchesProjectHashIncludingUnicode()
    {
        var project = new HaPlayProject
        {
            HaPlayVersion = "Prüfung 🎬",
        };
        var json = ProjectIO.Serialize(project);

        Assert.Equal(ProjectHash.Of(project), ProjectHash.OfSerializedJson(json));
    }
}

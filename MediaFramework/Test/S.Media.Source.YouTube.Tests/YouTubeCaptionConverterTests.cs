using Xunit;

namespace S.Media.Source.YouTube.Tests;

/// <summary>Covers the json3 → ASS conversion (the piece that recovers colour/alpha/style/positioning that
/// YoutubeExplode's flat-SRT path throws away) and the caption-URL format helper, all offline.</summary>
public sealed class YouTubeCaptionConverterTests
{
    // A compact json3 that exercises every mapped feature: a pen table (bold, red @ partial alpha), a window
    // position table (top-left via anchor 0), and events referencing them, including a multi-segment line.
    private const string Json3 = """
    {
      "wireMagic": "pb3",
      "pens": [
        {},
        { "bAttr": 1 },
        { "fcForeColor": 16711680, "foForeAlpha": 40 },
        { "iAttr": 1, "uAttr": 1 }
      ],
      "wpWinPositions": [
        {},
        { "apPoint": 0, "ahHorPos": 0, "avVerPos": 40 }
      ],
      "events": [
        { "tStartMs": 1000, "dDurationMs": 2000, "wpWinPosId": 1, "pPenId": 1, "segs": [ { "utf8": "Bold" } ] },
        { "tStartMs": 3000, "dDurationMs": 2000, "pPenId": 2, "segs": [ { "utf8": "Faint red" } ] },
        { "tStartMs": 5000, "dDurationMs": 2000, "segs": [ { "utf8": "plain " }, { "utf8": "styled", "pPenId": 3 } ] }
      ]
    }
    """;

    [Fact]
    public void Json3ToAss_EmitsWellFormedHeaderAndEvents()
    {
        var ass = YouTubeCaptionConverter.Json3ToAss(Json3, 1280, 720);

        Assert.Contains("[Script Info]", ass);
        Assert.Contains("PlayResX: 1280", ass);
        Assert.Contains("PlayResY: 720", ass);
        Assert.Contains("[V4+ Styles]", ass);
        Assert.Contains("[Events]", ass);
        Assert.Equal(3, ass.Split('\n').Count(l => l.StartsWith("Dialogue:")));
    }

    [Fact]
    public void Json3ToAss_MapsWindowPositionToAnchorAndPos()
    {
        var ass = YouTubeCaptionConverter.Json3ToAss(Json3, 1280, 720);
        var bold = ass.Split('\n').Single(l => l.StartsWith("Dialogue:") && l.Contains("Bold"));

        // anchor 0 (top-left) → \an7; ahHorPos 0% → x 0; avVerPos 40% of 720 → y 288.
        Assert.Contains("\\an7", bold);
        Assert.Contains("\\pos(0,288)", bold);
        Assert.Contains("\\b1", bold); // pen 1 bold
    }

    [Fact]
    public void Json3ToAss_MapsColourToBgrAndInvertsAlpha()
    {
        var ass = YouTubeCaptionConverter.Json3ToAss(Json3, 1280, 720);
        var red = ass.Split('\n').Single(l => l.Contains("Faint red"));

        // 0xFF0000 (RGB red) → ASS &HBBGGRR& = &H0000FF&; foForeAlpha 40 → \1a &H(255-40=215=D7)&.
        Assert.Contains("\\c&H0000FF&", red);
        Assert.Contains("\\1a&HD7&", red);
    }

    [Fact]
    public void Json3ToAss_AppliesPerSegmentPenWithinOneEvent()
    {
        var ass = YouTubeCaptionConverter.Json3ToAss(Json3, 1280, 720);
        var multi = ass.Split('\n').Single(l => l.Contains("styled"));

        // The "plain " seg is default (no italic/underline); the "styled" seg carries pen 3 (italic+underline).
        Assert.Contains("\\b0\\i0\\u0", multi); // plain segment resets fully
        Assert.Contains("\\i1\\u1", multi);     // styled segment
        Assert.Contains("plain ", multi);
        Assert.Contains("styled", multi);
    }

    [Fact]
    public void PlainCaptionsToAss_WrapsFlatCaptionsInValidAss()
    {
        var ass = YouTubeCaptionConverter.PlainCaptionsToAss(
        [
            ("Hello", 0, 1500),
            ("World", 1500, 1500),
        ]);

        Assert.Contains("[Events]", ass);
        var dialogues = ass.Split('\n').Where(l => l.StartsWith("Dialogue:")).ToArray();
        Assert.Equal(2, dialogues.Length);
        Assert.Contains("0:00:00.00,0:00:01.50", dialogues[0]);
        Assert.EndsWith("Hello", dialogues[0]);
        Assert.DoesNotContain("\\pos(", ass); // plain path never positions
    }

    [Theory]
    // No existing query → fmt appended.
    [InlineData("https://www.youtube.com/api/timedtext?v=abc&lang=en", "fmt=json3")]
    // Existing fmt is replaced, not duplicated.
    [InlineData("https://www.youtube.com/api/timedtext?v=abc&fmt=srv3", "fmt=json3")]
    public void WithCaptionFormat_SetsRequestedFormat(string url, string expectedFragment)
    {
        var result = YoutubeExplodeGateway.WithCaptionFormat(url, "json3");
        Assert.Contains(expectedFragment, result);
        Assert.Single(result.Split('&').Where(p => p.StartsWith("fmt=")));
    }

    [Fact]
    public void WithCaptionFormat_StripsXosfAndLegacyFormat()
    {
        var result = YoutubeExplodeGateway.WithCaptionFormat(
            "https://www.youtube.com/api/timedtext?v=abc&xosf=1&format=3&lang=en", "json3");

        Assert.DoesNotContain("xosf=", result);
        Assert.DoesNotContain("format=", result);
        Assert.Contains("fmt=json3", result);
        Assert.Contains("lang=en", result); // unrelated params preserved
    }
}

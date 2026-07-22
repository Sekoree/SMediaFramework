using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlValueCacheTests
{
    [Fact]
    public void SetNumber_ReportsChangeForFirstValue()
    {
        var cache = new ControlValueCache();

        var change = cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming);

        Assert.NotNull(change);
        Assert.Equal("x32", change!.Key.DeviceKey);
        Assert.Equal("/ch/01/mix/fader", change.Key.Address);
        Assert.Equal(ControlCachedValueKind.Number, change.Value.Kind);
        Assert.Equal(0.5, change.Value.NumberValue);
        Assert.Equal(ControlValueCacheSource.Incoming, change.Source);
    }

    [Fact]
    public void SetNumber_ReportsNoChangeWhenFreshValueIsRewrittenIdentically()
    {
        var cache = new ControlValueCache();
        cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming);

        var change = cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming);

        Assert.Null(change);
    }

    [Fact]
    public void SetNumber_ReportsChangeWhenValueDiffers()
    {
        var cache = new ControlValueCache();
        cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming);

        var change = cache.SetNumber("x32", "/ch/01/mix/fader", 0.6, ControlValueCacheSource.Incoming);

        Assert.NotNull(change);
        Assert.Equal(0.6, change!.Value.NumberValue);
    }

    [Fact]
    public void SetNumber_ReportsChangeWhenPreviouslyStaleValueBecomesFreshAgain()
    {
        var cache = new ControlValueCache();
        cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming);
        cache.MarkDeviceStale("x32");

        var change = cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming);

        Assert.NotNull(change);
        Assert.Equal(0.5, change!.Value.NumberValue);
    }

    [Fact]
    public void SetBoolean_ReportsChangeOnlyWhenStateFlips()
    {
        var cache = new ControlValueCache();

        var first = cache.SetBoolean("x32", "/ch/01/mix/on", true, ControlValueCacheSource.Incoming);
        var repeat = cache.SetBoolean("x32", "/ch/01/mix/on", true, ControlValueCacheSource.Incoming);
        var flipped = cache.SetBoolean("x32", "/ch/01/mix/on", false, ControlValueCacheSource.Incoming);

        Assert.NotNull(first);
        Assert.Null(repeat);
        Assert.NotNull(flipped);
        Assert.False(flipped!.Value.BooleanValue);
    }

    [Fact]
    public void VersionTracksSetStaleAndClearMutations()
    {
        var cache = new ControlValueCache();
        Assert.Equal(0, cache.Version);

        cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming);
        var afterSet = cache.Version;
        Assert.True(afterSet > 0);

        cache.MarkDeviceStale("x32");
        Assert.True(cache.Version > afterSet);
        var afterStale = cache.Version;

        cache.ClearDevice("x32");
        Assert.True(cache.Version > afterStale);
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public void StringKeyLookup_ReturnsEntryWithoutChangingPublicKeySemantics()
    {
        var cache = new ControlValueCache();
        cache.SetNumber("x32", "/ch/01/mix/fader", 0.5, ControlValueCacheSource.Incoming);

        Assert.True(cache.TryGet("x32", "/ch/01/mix/fader", out var entry));
        Assert.Equal(new ControlValueCacheKey("x32", "/ch/01/mix/fader"), entry.Key);
        Assert.False(cache.TryGet("X32", "/ch/01/mix/fader", out _)); // keys remain ordinal/case-sensitive
        Assert.Throws<ArgumentException>(() => cache.TryGet("", "/ch/01/mix/fader", out _));
    }
}

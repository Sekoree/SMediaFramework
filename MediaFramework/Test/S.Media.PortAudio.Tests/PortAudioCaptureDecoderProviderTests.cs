using System;
using Xunit;

namespace S.Media.PortAudio.Tests;

/// <summary>Covers the pure parsing/resolution logic of <c>PortAudioCaptureDecoderProvider</c> - the
/// <c>padev://</c> live-capture source provider added for the NXT-06 cutover (so a ShowSession cue can open a
/// PortAudio input device the same way it opens an NDI input). The actual capture-stream open is a hardware path
/// verified on a real device; these tests pin the scheme/name/format contract.</summary>
public sealed class PortAudioCaptureDecoderProviderTests
{
    [Theory]
    [InlineData("padev://Built-in Mic", true)]
    [InlineData("padev:Built-in Mic", true)]
    [InlineData("PADEV://x", true)]        // scheme is case-insensitive
    [InlineData("padev://", true)]
    [InlineData("ndi://cam", false)]
    [InlineData("file:///a.wav", false)]
    [InlineData("padevious://x", false)]   // must be the exact 'padev' scheme, not a prefix
    [InlineData("", false)]
    [InlineData("noscheme", false)]
    public void IsPaDevScheme_matches_only_the_padev_scheme(string uri, bool expected) =>
        Assert.Equal(expected, PortAudioCaptureDecoderProvider.IsPaDevScheme(uri));

    [Theory]
    [InlineData("padev://Built-in Mic", "Built-in Mic")]
    [InlineData("padev:Line In", "Line In")]
    [InlineData("padev://", "")]                       // empty → system default
    [InlineData("padev://My%20Mic", "My Mic")]         // URL-decoded
    [InlineData("padev://  Spaced  ", "Spaced")]        // trimmed
    public void ParseDeviceName_extracts_and_decodes_the_device_name(string uri, string expected) =>
        Assert.Equal(expected, PortAudioCaptureDecoderProvider.ParseDeviceName(uri));

    [Fact]
    public void ParseDescriptor_preserves_saved_capture_configuration()
    {
        var descriptor = PortAudioCaptureDecoderProvider.ParseDescriptor(
            "padev://USB%20Interface?hostApiName=JACK&hostApiIndex=2&globalDeviceIndex=7&channels=6&sampleRate=96000&latency=0.0125");

        Assert.Equal("USB Interface", descriptor.DeviceName);
        Assert.Equal("JACK", descriptor.HostApiName);
        Assert.Equal(2, descriptor.HostApiIndex);
        Assert.Equal(7, descriptor.GlobalDeviceIndex);
        Assert.Equal(6, descriptor.Channels);
        Assert.Equal(96000, descriptor.SampleRate);
        Assert.Equal(0.0125, descriptor.SuggestedLatencySeconds);
    }

    [Fact]
    public void ResolveDevice_emptyName_selectsTheDefaultInput()
    {
        var devices = new[]
        {
            new AudioDeviceInfo("0", "Mic A", MaxChannels: 1, DefaultSampleRate: 44100, IsDefault: false),
            new AudioDeviceInfo("1", "Mic B", MaxChannels: 8, DefaultSampleRate: 48000, IsDefault: true),
        };

        var (id, format) = PortAudioCaptureDecoderProvider.ResolveDevice(string.Empty, devices);

        Assert.Null(id);                       // null id = system default device
        Assert.Equal(48000, format.SampleRate); // from the default device
        Assert.Equal(2, format.Channels);       // 8 input channels clamped to stereo
    }

    [Fact]
    public void ResolveDevice_namedDevice_resolvesIdAndFormat()
    {
        var devices = new[]
        {
            new AudioDeviceInfo("0", "Mic A", MaxChannels: 1, DefaultSampleRate: 44100, IsDefault: true),
            new AudioDeviceInfo("7", "USB Interface", MaxChannels: 2, DefaultSampleRate: 96000, IsDefault: false),
        };

        var (id, format) = PortAudioCaptureDecoderProvider.ResolveDevice("usb interface", devices); // case-insensitive

        Assert.Equal("7", id);
        Assert.Equal(96000, format.SampleRate);
        Assert.Equal(2, format.Channels);
    }

    [Fact]
    public void ResolveDevice_configuredDescriptor_prefersSavedGlobalIndexAndFormat()
    {
        var devices = new[]
        {
            new AudioDeviceInfo("3", "USB Interface", MaxChannels: 2, DefaultSampleRate: 48000, IsDefault: true),
            new AudioDeviceInfo("7", "USB Interface", MaxChannels: 8, DefaultSampleRate: 48000, IsDefault: false),
        };
        var descriptor = new PortAudioCaptureDecoderProvider.CaptureDescriptor(
            "USB Interface", GlobalDeviceIndex: 7, Channels: 6, SampleRate: 96000,
            SuggestedLatencySeconds: 0.01);

        var (id, format) = PortAudioCaptureDecoderProvider.ResolveDevice(descriptor, devices);

        Assert.Equal("7", id);
        Assert.Equal(6, format.Channels);
        Assert.Equal(96000, format.SampleRate);
    }

    [Fact]
    public void ResolveCatalogDevice_prefersStableHostNameBeforeStaleIndexes()
    {
        var hosts = new[]
        {
            new PortAudioHostApiEntry(0, "ALSA", 0, 1, -1),
            new PortAudioHostApiEntry(4, "JACK", 0, 1, -1),
        };
        var devices = new[]
        {
            new PortAudioInputDeviceEntry(3, 0, "Interface", 2, 48000, 0.01, true),
            new PortAudioInputDeviceEntry(12, 4, "Interface", 8, 48000, 0.01, false),
        };
        var descriptor = new PortAudioCaptureDecoderProvider.CaptureDescriptor(
            "Interface", HostApiName: "jack", HostApiIndex: 1, GlobalDeviceIndex: 3,
            Channels: 6, SampleRate: 96000);

        var (id, format) = PortAudioCaptureDecoderProvider.ResolveCatalogDevice(descriptor, devices, hosts);

        Assert.Equal("12", id); // current JACK device wins over both stale numeric fallbacks
        Assert.Equal(new AudioFormat(96000, 6), format);
    }

    [Fact]
    public void ResolveDevice_monoDevice_keepsSingleChannel()
    {
        var devices = new[] { new AudioDeviceInfo("3", "Mono Mic", MaxChannels: 1, DefaultSampleRate: 16000, IsDefault: false) };

        var (_, format) = PortAudioCaptureDecoderProvider.ResolveDevice("Mono Mic", devices);

        Assert.Equal(16000, format.SampleRate);
        Assert.Equal(1, format.Channels);
    }

    [Fact]
    public void ResolveDevice_unknownName_throwsWithAvailableList()
    {
        var devices = new[] { new AudioDeviceInfo("0", "Mic A", MaxChannels: 2, DefaultSampleRate: 48000, IsDefault: true) };

        var ex = Assert.Throws<ArgumentException>(() =>
            PortAudioCaptureDecoderProvider.ResolveDevice("No Such Device", devices));
        Assert.Contains("No Such Device", ex.Message);
        Assert.Contains("Mic A", ex.Message); // surfaces what IS available
    }
}

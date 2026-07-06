using PMLib;
using PMLib.Types;
using S.Control;
using Xunit;
using Xunit.Abstractions;

namespace S.Control.Tests;

/// <summary>
/// MIDI-01: the PortMIDI capability diagnostic must give a truthful, non-throwing snapshot (present-or-not,
/// device counts) against the real native library, and — opt-in — a device round-trip must open/close a real
/// MIDI stream. The device-loop case self-skips when no MIDI device is attached (headless/CI), so the diagnostic
/// case is the always-on regression guard.
/// </summary>
public sealed class MIDIRuntimeDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public void Query_ReturnsAWellFormedCapability_WithoutThrowing()
    {
        var cap = MIDIRuntimeDiagnostics.Query();
        output.WriteLine($"PortMIDI capability: {cap}");

        if (cap.Available)
        {
            Assert.True(cap.InputDeviceCount >= 0);
            Assert.True(cap.OutputDeviceCount >= 0);
            Assert.Null(cap.Detail); // no error message when available
        }
        else
        {
            // If PortMIDI could not initialize, that must be reported (not silently zero-with-no-reason).
            Assert.False(string.IsNullOrEmpty(cap.Detail));
            Assert.Equal(0, cap.InputDeviceCount);
            Assert.Equal(0, cap.OutputDeviceCount);
        }
    }

    [Fact]
    public void Query_IsRepeatable_AndLeavesTheLibraryTerminated()
    {
        // The query acquires + releases the shared PortMIDI lease each time; repeated calls must not leak the
        // initialization or start failing (ref-count balance).
        var first = MIDIRuntimeDiagnostics.Query();
        var second = MIDIRuntimeDiagnostics.Query();
        Assert.Equal(first.Available, second.Available);
        if (first.Available)
            Assert.Equal(first.OutputDeviceCount, second.OutputDeviceCount);
    }

    // Opt-in real device round-trip: needs an actual MIDI output device. Set MFP_RUN_MIDI_TESTS=1 to run;
    // self-skips when opted out or when no device is attached, so it never flakes the hermetic suite.
    [Fact]
    public void OutputDevice_OpensAndClosesCleanly_WhenPresent()
    {
        if (Environment.GetEnvironmentVariable("MFP_RUN_MIDI_TESTS") != "1")
        {
            output.WriteLine("skipped: set MFP_RUN_MIDI_TESTS=1 to run the real MIDI device round-trip");
            return;
        }

        Assert.Equal(PmError.NoError, PMUtil.Initialize());
        try
        {
            var outputs = PMUtil.GetOutputDevices();
            if (outputs.Count == 0)
            {
                output.WriteLine("skipped: no MIDI output device attached");
                return;
            }

            var device = outputs[0];
            output.WriteLine($"opening MIDI output '{device.Name}' (id {device.Id})");
            Assert.Equal(PmError.NoError, PMUtil.OpenOutput(out var stream, device.Id));
            try
            {
                // A middle-C note-on then note-off — exercises the write path; harmless on any synth.
                Assert.Equal(PmError.NoError, PMUtil.WriteShort(stream, 0, 0x00_3C_90u)); // note on, ch1, C4
                Assert.Equal(PmError.NoError, PMUtil.WriteShort(stream, 0, 0x00_3C_80u)); // note off
            }
            finally
            {
                Assert.Equal(PmError.NoError, PMUtil.Close(stream));
            }
        }
        finally
        {
            PMUtil.Terminate();
        }
    }
}

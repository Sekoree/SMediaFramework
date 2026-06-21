using NDILib;
using PMLib;
using PMLib.Types;

namespace HaPlay;

internal sealed record RuntimeModuleStatus(bool IsAvailable, string? Detail);

internal static class RuntimeModules
{
    private static readonly Lazy<RuntimeModuleStatus> Ndi = new(ProbeNdi, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<RuntimeModuleStatus> Midi = new(ProbeMidi, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsNdiAvailable => Ndi.Value.IsAvailable;
    public static string? NdiUnavailableReason => Ndi.Value.IsAvailable ? null : Ndi.Value.Detail;

    public static bool IsMidiAvailable => Midi.Value.IsAvailable;
    public static string? MidiUnavailableReason => Midi.Value.IsAvailable ? null : Midi.Value.Detail;

    private static RuntimeModuleStatus ProbeNdi()
    {
        try
        {
            if (!NDIRuntime.IsSupportedCpu())
                return new RuntimeModuleStatus(false, "NDI runtime unavailable: CPU is not supported.");

            var version = NDIRuntime.Version;
            var rc = NDIRuntime.Create(out var runtime);
            runtime?.Dispose();
            if (rc != 0)
                return new RuntimeModuleStatus(false, $"NDI runtime unavailable: init returned {rc}.");

            return new RuntimeModuleStatus(true,
                string.IsNullOrWhiteSpace(version) ? "NDI runtime available." : $"NDI {version}");
        }
        catch (Exception ex)
        {
            return new RuntimeModuleStatus(false, $"NDI runtime unavailable: {ex.Message}");
        }
    }

    private static RuntimeModuleStatus ProbeMidi()
    {
        try
        {
            var err = PMUtil.Initialize();
            if (err != PmError.NoError)
                return new RuntimeModuleStatus(false,
                    $"MIDI runtime unavailable: {PMUtil.GetErrorText(err) ?? err.ToString()}.");

            try
            {
                _ = PMUtil.CountDevices();
                return new RuntimeModuleStatus(true, "MIDI runtime available.");
            }
            finally
            {
                PMUtil.Terminate();
            }
        }
        catch (Exception ex)
        {
            return new RuntimeModuleStatus(false, $"MIDI runtime unavailable: {ex.Message}");
        }
    }
}

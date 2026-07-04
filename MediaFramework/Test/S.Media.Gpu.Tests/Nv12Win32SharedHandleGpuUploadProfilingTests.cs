using S.Media.Gpu.Diagnostics;
using Xunit;

namespace S.Media.Gpu.Tests;

public sealed class Nv12Win32SharedHandleGpuUploadProfilingTests
{
    [Fact]
    public void AfterModuleInit_ProfilingDisabledByDefault()
    {
        Assert.False(Nv12Win32SharedHandleGpuUploadProfiling.IsEnabled);
    }

    [Fact]
    public void TestOverride_EnableThenReset_CountersTrackInteropStagingAndFailure()
    {
        Nv12Win32SharedHandleGpuUploadProfiling.SetTestOverride(true);
        try
        {
            Nv12Win32SharedHandleGpuUploadProfiling.ResetCounters();

            Nv12Win32SharedHandleGpuUploadProfiling.RecordUploadAttempt();
            Nv12Win32SharedHandleGpuUploadProfiling.RecordInteropSuccess();

            Nv12Win32SharedHandleGpuUploadProfiling.RecordUploadAttempt();
            Nv12Win32SharedHandleGpuUploadProfiling.RecordInteropMissBeforeStaging();
            Nv12Win32SharedHandleGpuUploadProfiling.RecordStagingSuccess();

            Nv12Win32SharedHandleGpuUploadProfiling.RecordUploadAttempt();
            Nv12Win32SharedHandleGpuUploadProfiling.RecordInteropMissBeforeStaging();
            Nv12Win32SharedHandleGpuUploadProfiling.RecordBothPathsFailed();

            Assert.Equal(3, Nv12Win32SharedHandleGpuUploadProfiling.UploadAttempts);
            Assert.Equal(1, Nv12Win32SharedHandleGpuUploadProfiling.UploadInteropSuccess);
            Assert.Equal(1, Nv12Win32SharedHandleGpuUploadProfiling.UploadStagingSuccess);
            Assert.Equal(2, Nv12Win32SharedHandleGpuUploadProfiling.UploadInteropFailedBeforeStaging);
            Assert.Equal(1, Nv12Win32SharedHandleGpuUploadProfiling.UploadBothPathsFailed);
        }
        finally
        {
            Nv12Win32SharedHandleGpuUploadProfiling.ResetCounters();
            Nv12Win32SharedHandleGpuUploadProfiling.SetTestOverride(null);
        }
    }
}

using Android.App;
using Android.Content;
using Android.Content.PM;

namespace HaViz.Android;

/// <summary>Outcome of one StartActivityForResult round-trip.</summary>
public sealed record ActivityResultData(Result ResultCode, Intent? Data);

/// <summary>
/// Task-based activity-result and runtime-permission plumbing, shared by the SAF folder picker
/// and the MediaProjection consent flow: each call takes a request code from the registry and the
/// overrides complete the matching pending TaskCompletionSource.
/// </summary>
public partial class MainActivity
{
    // Request codes must fit in 16 bits (the framework masks them); this block sits away from the
    // low codes Avalonia/AndroidX use internally.
    private const int FirstRequestCode = 0xB000;
    private const int LastRequestCode = 0xBFFF;

    private readonly object _resultLock = new();
    private readonly Dictionary<int, TaskCompletionSource<ActivityResultData>> _pendingActivityResults = [];
    private readonly Dictionary<int, TaskCompletionSource<bool>> _pendingPermissionRequests = [];
    private int _nextRequestCode = FirstRequestCode;

    public Task<ActivityResultData> StartActivityForResultAsync(Intent intent)
    {
        var tcs = new TaskCompletionSource<ActivityResultData>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requestCode;
        lock (_resultLock)
        {
            requestCode = NextRequestCode();
            _pendingActivityResults[requestCode] = tcs;
        }

        try
        {
            StartActivityForResult(intent, requestCode);
        }
        catch
        {
            lock (_resultLock)
                _pendingActivityResults.Remove(requestCode);
            throw;
        }

        return tcs.Task;
    }

    /// <summary>True when the permission is (or becomes) granted; false on user denial.</summary>
    public Task<bool> EnsurePermissionAsync(string permission)
    {
        if (CheckSelfPermission(permission) == Permission.Granted)
            return Task.FromResult(true);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requestCode;
        lock (_resultLock)
        {
            requestCode = NextRequestCode();
            _pendingPermissionRequests[requestCode] = tcs;
        }

        RequestPermissions([permission], requestCode);
        return tcs.Task;
    }

    private int NextRequestCode()
    {
        if (_nextRequestCode > LastRequestCode)
            _nextRequestCode = FirstRequestCode;
        return _nextRequestCode++;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        TaskCompletionSource<ActivityResultData>? tcs;
        lock (_resultLock)
            _pendingActivityResults.Remove(requestCode, out tcs);
        tcs?.TrySetResult(new ActivityResultData(resultCode, data));
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        TaskCompletionSource<bool>? tcs;
        lock (_resultLock)
            _pendingPermissionRequests.Remove(requestCode, out tcs);
        tcs?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
    }
}

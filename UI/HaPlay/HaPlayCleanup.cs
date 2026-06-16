using System;
using System.Threading;
using S.Media.Core.Diagnostics;

namespace HaPlay;

/// <summary>
/// HaPlay-local best-effort teardown helpers. Centralizes the scattered
/// <c>try { … } catch { }</c> cleanup blocks so a failure during shutdown is
/// surfaced in <c>DEBUG</c> (and routed through the app logger) instead of being
/// silently swallowed, while keeping the same best-effort behaviour in release.
/// </summary>
/// <remarks>
/// Delegates to <see cref="MediaDiagnostics.SwallowDisposeErrors"/> so HaPlay's
/// teardown policy matches the framework's own (issues-and-improvements #6). Prefer
/// these over bare <c>catch { }</c> in any new best-effort dispose/cancel path.
/// </remarks>
internal static class HaPlayCleanup
{
    /// <summary>Dispose <paramref name="disposable"/> best-effort; <c>null</c> is ignored.</summary>
    public static void TryDispose(IDisposable? disposable, string context)
    {
        if (disposable is null)
            return;
        MediaDiagnostics.SwallowDisposeErrors(disposable.Dispose, context);
    }

    /// <summary>
    /// Run an arbitrary best-effort teardown action (unsubscribe, delete a temp file,
    /// dispose a member without a static <see cref="IDisposable"/> type, …).
    /// </summary>
    public static void TryRun(string context, Action action)
        => MediaDiagnostics.SwallowDisposeErrors(action, context);

    /// <summary>
    /// Best-effort <see cref="CancellationTokenSource.Cancel()"/>. A CTS already disposed
    /// during teardown is common, so the throw is swallowed (logged in <c>DEBUG</c>).
    /// </summary>
    public static void TryCancel(CancellationTokenSource? cts, string context)
    {
        if (cts is null)
            return;
        MediaDiagnostics.SwallowDisposeErrors(cts.Cancel, context);
    }
}

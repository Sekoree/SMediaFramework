using Avalonia.Headless;

namespace HaPlay.Tests;

/// <summary>
/// Awaits async bodies dispatched onto the headless UI session - the body itself, not just its
/// scheduling. <see cref="HeadlessUnitTestSession"/> has no <c>Func&lt;Task&gt;</c> overload of
/// <c>Dispatch</c>: an <c>async () =&gt; …</c> lambda binds to <c>Dispatch&lt;TResult&gt;(Func&lt;TResult&gt;)</c>
/// with <c>TResult = Task</c>, which runs the lambda only to its first await and returns the inner
/// task un-awaited. Every assertion after that first await then ran (or failed) after the test had
/// already passed, and exceptions from the body were lost. Always use these helpers for async
/// bodies; plain <c>Dispatch</c> stays fine for synchronous ones.
/// </summary>
internal static class HeadlessDispatchExtensions
{
    public static Task DispatchAsync(
        this HeadlessUnitTestSession session, Func<Task> body, CancellationToken cancellationToken = default)
        // Route through the Func<Task<TResult>> overload: it keeps pumping the session's dispatcher
        // until the inner task completes. A naive `await await session.Dispatch(body, ct)` deadlocks -
        // once the lambda hits its first await the session stops pumping, so the body's continuation
        // (queued back onto that dispatcher) would never run.
        => session.Dispatch<object?>(async () => { await body(); return null; }, cancellationToken);

    public static async Task<TResult> DispatchAsync<TResult>(
        this HeadlessUnitTestSession session, Func<Task<TResult>> body, CancellationToken cancellationToken = default)
        // The Func<Task<TResult>> overload of Dispatch awaits the inner task itself.
        => await session.Dispatch(body, cancellationToken);
}

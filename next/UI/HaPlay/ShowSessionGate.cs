namespace HaPlay;

/// <summary>
/// The Phase-8 convergence runtime gate, in ONE place so the cue workspace and the media-player deck can never
/// disagree about which engine is live.
/// <para>As of the 2026-07-01 flip, the headless <see cref="S.Media.Session.ShowSession"/> path is the
/// <b>default</b> for both. The ported <c>CuePlaybackEngine</c> / <c>HaPlayPlaybackSession</c> /
/// <c>SoundboardEngine</c> remain only as a fallback: set <c>HAPLAY_USE_SHOWSESSION=0</c> (or
/// <c>false</c>/<c>off</c>/<c>no</c>) to force the legacy engines — the instant, no-rebuild escape hatch while the
/// new path soaks in real use. Any other value, including unset, uses ShowSession.</para>
/// </summary>
public static class ShowSessionGate
{
    /// <summary>The environment variable that opts OUT of the ShowSession default.</summary>
    public const string EnvVar = "HAPLAY_USE_SHOWSESSION";

    /// <summary>True when the ShowSession convergence path should run (the default). False only when the operator
    /// explicitly opts out via <see cref="EnvVar"/> set to <c>0</c>/<c>false</c>/<c>off</c>/<c>no</c>.</summary>
    public static bool UseShowSession => IsEnabled(Environment.GetEnvironmentVariable(EnvVar));

    /// <summary>Pure decision (testable): unset ⇒ on; explicit falsey value ⇒ off; anything else ⇒ on.</summary>
    internal static bool IsEnabled(string? value) =>
        value is null
        || !(value.Equals("0", StringComparison.Ordinal)
             || value.Equals("false", StringComparison.OrdinalIgnoreCase)
             || value.Equals("off", StringComparison.OrdinalIgnoreCase)
             || value.Equals("no", StringComparison.OrdinalIgnoreCase));

    /// <summary>Human-readable description of the opt-out state for a startup log line.</summary>
    public static string DescribeOptOut()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        return value is null ? "unset" : $"'{value}'";
    }
}

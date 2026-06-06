using System.Text.Json;
using System.Text.Json.Serialization;

namespace S.Media.Playback;

public enum CueFaultPolicy
{
    StopShow,
    SkipCue,
    Continue,
    HoldLastFrame,
    FadeToBlackOrSilence,
    ContinueAudioOnly,
    ContinueVideoOnly,
    RouteToFallbackOutput,
}

public enum CueExecutionStatus
{
    Fired,
    SkippedDisabled,
    SkippedNotArmed,
    NotReady,
    Failed,
}

public sealed record CueDefinition(
    string Id,
    int Number,
    string Label,
    bool Armed = true,
    bool Enabled = true,
    TimeSpan PreWait = default,
    TimeSpan PostWait = default,
    string? GroupId = null,
    string? FollowOnCueId = null,
    IReadOnlyList<string>? StopTargetIds = null,
    bool AutoContinue = false,
    string? PreloadKey = null,
    string? FallbackOutputId = null,
    CueFaultPolicy FaultPolicy = CueFaultPolicy.StopShow);

public sealed record CueShowFile(
    int Version,
    IReadOnlyList<CueDefinition> Cues,
    IReadOnlyList<OutputPatchRoute> Outputs,
    IReadOnlyList<OutputPatchRoute> Routes,
    IReadOnlyList<string> Devices);

// Source-generated, NativeAOT-safe contract for the cue show file (default PascalCase naming, indented).
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CueShowFile))]
internal partial class CueShowFileJsonContext : JsonSerializerContext;

public sealed record CueExecutionLogEntry(
    string CueId,
    int Number,
    string Label,
    CueExecutionStatus Status,
    DateTimeOffset Timestamp,
    string? Message);

public sealed class CueGraph
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CueEntry> _cues = new(StringComparer.Ordinal);
    private readonly List<CueExecutionLogEntry> _log = [];

    public IReadOnlyList<CueDefinition> Cues
    {
        get
        {
            lock (_gate)
                return _cues.Values.Select(e => e.Definition).OrderBy(c => c.Number).ToArray();
        }
    }

    public IReadOnlyList<CueExecutionLogEntry> ExecutionLog
    {
        get
        {
            lock (_gate)
                return _log.ToArray();
        }
    }

    public IReadOnlyList<CueDefinition> GetGroup(string groupId)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        lock (_gate)
            return _cues.Values
                .Select(e => e.Definition)
                .Where(c => string.Equals(c.GroupId, groupId, StringComparison.Ordinal))
                .OrderBy(c => c.Number)
                .ToArray();
    }

    public void AddCue(
        CueDefinition definition,
        Func<CancellationToken, ValueTask> action,
        Func<bool>? isReady = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(definition.Id);
        ArgumentException.ThrowIfNullOrEmpty(definition.Label);
        ArgumentNullException.ThrowIfNull(action);

        lock (_gate)
        {
            if (!_cues.TryAdd(definition.Id, new CueEntry(definition, action, isReady)))
                throw new ArgumentException($"cue '{definition.Id}' is already registered", nameof(definition));
        }
    }

    public bool TryGetCue(string cueId, out CueDefinition definition)
    {
        ArgumentException.ThrowIfNullOrEmpty(cueId);
        lock (_gate)
        {
            if (_cues.TryGetValue(cueId, out var entry))
            {
                definition = entry.Definition;
                return true;
            }
        }

        definition = default!;
        return false;
    }

    public bool SetCueState(string cueId, bool? armed = null, bool? enabled = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cueId);
        lock (_gate)
        {
            if (!_cues.TryGetValue(cueId, out var entry))
                return false;

            _cues[cueId] = entry with
            {
                Definition = entry.Definition with
                {
                    Armed = armed ?? entry.Definition.Armed,
                    Enabled = enabled ?? entry.Definition.Enabled,
                },
            };
            return true;
        }
    }

    public async ValueTask<CueExecutionStatus> FireAsync(string cueId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(cueId);
        var entry = GetEntry(cueId);
        if (entry is null)
            throw new ArgumentException($"cue '{cueId}' is not registered", nameof(cueId));

        return await FireEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<string> PanicStopTargets()
    {
        lock (_gate)
            return _cues.Values
                .SelectMany(e => e.Definition.StopTargetIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
    }

    public IReadOnlyList<string> PrewarmKeys()
    {
        lock (_gate)
            return _cues.Values
                .Select(e => e.Definition.PreloadKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
    }

    public CueShowFile ToShowFile(
        IEnumerable<OutputPatchRoute>? outputs = null,
        IEnumerable<OutputPatchRoute>? routes = null,
        IEnumerable<string>? devices = null) =>
        new(1, Cues, (outputs ?? []).ToArray(), (routes ?? []).ToArray(), (devices ?? []).ToArray());

    public string SerializeShowFile(CueShowFile? showFile = null) =>
        JsonSerializer.Serialize(showFile ?? ToShowFile(), CueShowFileJsonContext.Default.CueShowFile);

    public static CueShowFile DeserializeShowFile(string json) =>
        JsonSerializer.Deserialize(json, CueShowFileJsonContext.Default.CueShowFile)
        ?? throw new InvalidOperationException("show file JSON did not contain a valid cue show.");

    private async ValueTask<CueExecutionStatus> FireEntryAsync(CueEntry entry, CancellationToken cancellationToken)
    {
        var cue = entry.Definition;
        if (!cue.Enabled)
            return Log(cue, CueExecutionStatus.SkippedDisabled, "cue is disabled");
        if (!cue.Armed)
            return Log(cue, CueExecutionStatus.SkippedNotArmed, "cue is not armed");
        if (entry.IsReady is not null && !entry.IsReady())
            return Log(cue, CueExecutionStatus.NotReady, "cue is not ready");

        try
        {
            if (cue.PreWait > TimeSpan.Zero)
                await Task.Delay(cue.PreWait, cancellationToken).ConfigureAwait(false);

            await entry.Action(cancellationToken).ConfigureAwait(false);
            Log(cue, CueExecutionStatus.Fired, null);

            if (cue.PostWait > TimeSpan.Zero)
                await Task.Delay(cue.PostWait, cancellationToken).ConfigureAwait(false);

            if (cue.AutoContinue && cue.FollowOnCueId is not null)
            {
                var follow = GetEntry(cue.FollowOnCueId)
                    ?? throw new InvalidOperationException($"follow-on cue '{cue.FollowOnCueId}' is not registered.");
                await FireEntryAsync(follow, cancellationToken).ConfigureAwait(false);
            }

            return CueExecutionStatus.Fired;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(cue, CueExecutionStatus.Failed, ex.Message);
            if (cue.FaultPolicy == CueFaultPolicy.StopShow)
                throw;
            return CueExecutionStatus.Failed;
        }
    }

    private CueEntry? GetEntry(string cueId)
    {
        lock (_gate)
            return _cues.GetValueOrDefault(cueId);
    }

    private CueExecutionStatus Log(CueDefinition cue, CueExecutionStatus status, string? message)
    {
        lock (_gate)
        {
            _log.Add(new CueExecutionLogEntry(
                cue.Id,
                cue.Number,
                cue.Label,
                status,
                DateTimeOffset.UtcNow,
                message));
        }
        return status;
    }

    private sealed record CueEntry(
        CueDefinition Definition,
        Func<CancellationToken, ValueTask> Action,
        Func<bool>? IsReady);
}

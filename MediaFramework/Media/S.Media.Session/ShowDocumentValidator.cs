namespace S.Media.Session;

/// <summary>Thrown when a <see cref="ShowDocument"/> fails validation at load. The running show is left
/// untouched - validation happens before any teardown (NXT-12), so a malformed document can never destroy
/// a live show or leave a half-built replacement.</summary>
public sealed class ShowDocumentValidationException(IReadOnlyList<string> errors)
    : Exception("show document is invalid:" + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", errors))
{
    /// <summary>Every problem found, so a caller/editor can surface them all at once.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Validates a <see cref="ShowDocument"/>'s referential and semantic invariants before it is loaded (NXT-12):
/// supported version, unique cue ids/numbers, single clip per cue, references that resolve, acyclic
/// auto-continue chains (NXT-07), and sane composition dimensions/rates. <see cref="Validate"/> returns every
/// problem found; <see cref="ThrowIfInvalid"/> throws a <see cref="ShowDocumentValidationException"/> when any
/// exist. Pure and allocation-light so it is cheap to run on every load.
/// </summary>
public static class ShowDocumentValidator
{
    /// <summary>The single schema version this build understands.</summary>
    public const int SupportedVersion = 1;

    /// <summary>Validates <paramref name="document"/> and returns every problem found (empty ⇒ valid).</summary>
    public static IReadOnlyList<string> Validate(ShowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();

        if (document.Version != SupportedVersion)
            errors.Add($"unsupported document version {document.Version} (this build supports version {SupportedVersion}).");

        // Cues: non-empty unique ids, and unique numbers (GO advances by number, so duplicates break the cursor).
        var cueIds = new HashSet<string>(StringComparer.Ordinal);
        var cueNumbers = new HashSet<int>();
        foreach (var cue in document.Cues ?? [])
        {
            if (string.IsNullOrEmpty(cue.Id))
                errors.Add("a cue has an empty id.");
            else if (!cueIds.Add(cue.Id))
                errors.Add($"duplicate cue id '{cue.Id}'.");
            if (string.IsNullOrEmpty(cue.Label))
                errors.Add($"cue '{cue.Id}' has an empty label.");
            if (!cueNumbers.Add(cue.Number))
                errors.Add($"duplicate cue number {cue.Number} - GO uses the number as its cursor, so it must be unique.");
        }

        // Compositions: unique ids, positive dimensions and frame rate.
        var compIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in document.Compositions ?? [])
        {
            if (string.IsNullOrEmpty(comp.Id))
                errors.Add("a composition has an empty id.");
            else if (!compIds.Add(comp.Id))
                errors.Add($"duplicate composition id '{comp.Id}'.");
            if (comp.Width <= 0 || comp.Height <= 0)
                errors.Add($"composition '{comp.Id}' has non-positive dimensions {comp.Width}x{comp.Height}.");
            if (comp.FrameRateNum <= 0 || comp.FrameRateDen <= 0)
                errors.Add($"composition '{comp.Id}' has a non-positive frame rate {comp.FrameRateNum}/{comp.FrameRateDen}.");
        }

        // Clips: at most one per cue (the runtime keys clips by cue id - a duplicate throws at load), and every
        // clip must reference an existing cue and, if placed, an existing composition.
        var clipCues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in document.Clips ?? [])
        {
            if (!clipCues.Add(clip.CueId))
                errors.Add($"more than one clip binds cue '{clip.CueId}' - a cue binds at most one clip.");
            if (!string.IsNullOrEmpty(clip.CueId) && !cueIds.Contains(clip.CueId))
                errors.Add($"a clip binds unknown cue '{clip.CueId}'.");
            if (clip.CompositionId is { Length: > 0 } cid && !compIds.Contains(cid))
                errors.Add($"the clip for cue '{clip.CueId}' references unknown composition '{cid}'.");

            // DOC-01: scalar/path sanity so a malformed clip is caught at load, not silently mis-played.
            if (string.IsNullOrWhiteSpace(clip.MediaPath))
                errors.Add($"the clip for cue '{clip.CueId}' has an empty media path.");
            if (clip.StartOffset < TimeSpan.Zero)
                errors.Add($"the clip for cue '{clip.CueId}' has a negative start offset.");
            if (clip.EndOffset < TimeSpan.Zero)
                errors.Add($"the clip for cue '{clip.CueId}' has a negative end offset.");
            if (clip.FadeIn < TimeSpan.Zero)
                errors.Add($"the clip for cue '{clip.CueId}' has a negative fade-in.");
            if (clip.FadeOut < TimeSpan.Zero)
                errors.Add($"the clip for cue '{clip.CueId}' has a negative fade-out.");
            if (clip.LayerIndex < 0)
                errors.Add($"the clip for cue '{clip.CueId}' has a negative layer index {clip.LayerIndex}.");
            if (clip.AudioStreamIndex is { } asi && asi < -1)
                errors.Add($"the clip for cue '{clip.CueId}' has an audio stream index {asi} below -1 (use -1 for none, null for auto).");
            foreach (var sub in clip.GetSubtitleSelections())
                if (sub.StreamIndex < -1)
                    errors.Add($"the clip for cue '{clip.CueId}' has a subtitle stream index {sub.StreamIndex} below -1.");
            if (clip.Placement is { } placement)
                ValidatePlacement(clip.CueId, "its placement", placement, errors);

            // A clip may fan its video onto several compositions (ExtraPlacements); every one must resolve too,
            // else the placement is silently dropped at play time instead of caught at load.
            foreach (var extra in clip.ExtraPlacements ?? [])
            {
                if (string.IsNullOrEmpty(extra.CompositionId))
                    errors.Add($"the clip for cue '{clip.CueId}' has an extra placement with an empty composition id.");
                else if (!compIds.Contains(extra.CompositionId))
                    errors.Add($"the clip for cue '{clip.CueId}' has an extra placement on unknown composition '{extra.CompositionId}'.");
                if (extra.LayerIndex < 0)
                    errors.Add($"the clip for cue '{clip.CueId}' has an extra placement with a negative layer index {extra.LayerIndex}.");
                if (extra.Placement is { } extraPlacement)
                    ValidatePlacement(clip.CueId, $"its placement on '{extra.CompositionId}'", extraPlacement, errors);
            }

            foreach (var audioRoute in clip.AudioRoutes ?? [])
            {
                if (!float.IsFinite(audioRoute.Gain) || audioRoute.Gain < 0f)
                    errors.Add($"the clip for cue '{clip.CueId}' has an invalid audio-route gain.");
                if (audioRoute.SampleRate is { } rate && rate <= 0)
                    errors.Add($"the clip for cue '{clip.CueId}' has a non-positive audio route sample rate {rate}.");
                if (audioRoute.MatrixOutputChannels is <= 0)
                    errors.Add($"the clip for cue '{clip.CueId}' has a non-positive audio matrix output count.");
                foreach (var cell in audioRoute.MatrixCells ?? [])
                {
                    if (cell.InputChannel < 0 || cell.OutputChannel < 0
                        || audioRoute.MatrixOutputChannels is { } outputs && cell.OutputChannel >= outputs)
                        errors.Add($"the clip for cue '{clip.CueId}' has an audio matrix cell outside its declared dimensions.");
                    if (!float.IsFinite(cell.Gain) || cell.Gain < 0f)
                        errors.Add($"the clip for cue '{clip.CueId}' has an invalid audio matrix cell gain.");
                }
            }
        }

        ValidateFollowOn(document.Cues ?? [], cueIds, errors);

        // Stop targets must reference existing cues.
        foreach (var cue in document.Cues ?? [])
            foreach (var target in cue.StopTargetIds ?? [])
                if (!cueIds.Contains(target))
                    errors.Add($"cue '{cue.Id}' lists unknown stop-target cue '{target}'.");

        // Audio outputs: unique ids. Routes: an enabled route's SourceId is a cue id and its OutputId must be a
        // declared audio output or the implicit master - a dangling route otherwise silently never matches at
        // play time instead of being caught at load (NXT-25).
        var audioOutputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in document.AudioOutputs ?? [])
        {
            if (string.IsNullOrEmpty(output.Id))
                errors.Add("an audio output has an empty id.");
            else if (!audioOutputIds.Add(output.Id))
                errors.Add($"duplicate audio output id '{output.Id}'.");
            if (string.IsNullOrWhiteSpace(output.GroupId))
                errors.Add($"audio output '{output.Id}' has an empty group id.");
        }

        foreach (var route in document.Routes ?? [])
        {
            if (!route.Enabled)
                continue;
            if (!cueIds.Contains(route.SourceId))
                errors.Add($"route '{route.SourceId}' → '{route.OutputId}' references an unknown cue.");
            if (!string.Equals(route.OutputId, ShowSession.MasterOutputId, StringComparison.Ordinal)
                && !audioOutputIds.Contains(route.OutputId))
                errors.Add($"route '{route.SourceId}' → '{route.OutputId}' references an undeclared audio output.");
        }

        return errors;
    }

    /// <summary>DOC-01: a placement's geometry must be finite and in range so the compositor is never handed
    /// a NaN/Infinity transform, a collapsed/negative dest rect, or crops that erase the whole frame.</summary>
    private static void ValidatePlacement(string cueId, string where, ShowVideoPlacement p, List<string> errors)
    {
        void Finite(double v, string name)
        {
            if (!double.IsFinite(v))
                errors.Add($"the clip for cue '{cueId}' has a non-finite {name} in {where}.");
        }

        Finite(p.DestX, "dest X");
        Finite(p.DestY, "dest Y");
        Finite(p.DestWidth, "dest width");
        Finite(p.DestHeight, "dest height");
        Finite(p.Opacity, "opacity");
        Finite(p.RotationDegrees, "rotation");
        Finite(p.CropLeft, "left crop");
        Finite(p.CropTop, "top crop");
        Finite(p.CropRight, "right crop");
        Finite(p.CropBottom, "bottom crop");

        if (double.IsFinite(p.DestWidth) && p.DestWidth <= 0)
            errors.Add($"the clip for cue '{cueId}' has a non-positive dest width in {where}.");
        if (double.IsFinite(p.DestHeight) && p.DestHeight <= 0)
            errors.Add($"the clip for cue '{cueId}' has a non-positive dest height in {where}.");
        if (double.IsFinite(p.Opacity) && p.Opacity is < 0 or > 1)
            errors.Add($"the clip for cue '{cueId}' has an opacity {p.Opacity} outside [0, 1] in {where}.");

        CheckCrop(p.CropLeft, "left crop");
        CheckCrop(p.CropTop, "top crop");
        CheckCrop(p.CropRight, "right crop");
        CheckCrop(p.CropBottom, "bottom crop");
        if (double.IsFinite(p.CropLeft) && double.IsFinite(p.CropRight) && p.CropLeft + p.CropRight >= 1)
            errors.Add($"the clip for cue '{cueId}' has horizontal crops that erase the whole frame in {where}.");
        if (double.IsFinite(p.CropTop) && double.IsFinite(p.CropBottom) && p.CropTop + p.CropBottom >= 1)
            errors.Add($"the clip for cue '{cueId}' has vertical crops that erase the whole frame in {where}.");

        void CheckCrop(double v, string name)
        {
            if (double.IsFinite(v) && v is < 0 or > 1)
                errors.Add($"the clip for cue '{cueId}' has a {name} {v} outside [0, 1] in {where}.");
        }
    }

    /// <summary>Throws <see cref="ShowDocumentValidationException"/> if <paramref name="document"/> is invalid.</summary>
    public static void ThrowIfInvalid(ShowDocument document)
    {
        var errors = Validate(document);
        if (errors.Count > 0)
            throw new ShowDocumentValidationException(errors);
    }

    /// <summary>Checks that follow-on links resolve, and that the <em>auto-continue</em> subgraph (the only one
    /// that recurses in <see cref="CueGraph"/>) is acyclic - a cycle would auto-continue forever.</summary>
    private static void ValidateFollowOn(IReadOnlyList<CueDefinition> cues, HashSet<string> cueIds, List<string> errors)
    {
        // Out-degree ≤ 1 functional graph over auto-continue follow-on edges.
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cue in cues)
        {
            if (cue.FollowOnCueId is not { } follow)
                continue;
            if (!cueIds.Contains(follow))
                errors.Add($"cue '{cue.Id}' has an unknown follow-on cue '{follow}'.");
            else if (cue.AutoContinue && !string.IsNullOrEmpty(cue.Id))
                next[cue.Id] = follow; // only auto-continue links recurse; a plain follow-on is a GO target
        }

        var settled = new HashSet<string>(StringComparer.Ordinal); // walked already, proven cycle-free
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in next.Keys)
        {
            if (settled.Contains(start))
                continue;
            var path = new HashSet<string>(StringComparer.Ordinal);
            var node = start;
            while (node is not null && !settled.Contains(node))
            {
                if (!path.Add(node))
                {
                    if (reported.Add(node))
                        errors.Add($"the auto-continue follow-on chain through cue '{node}' contains a cycle (it would never terminate).");
                    break;
                }
                node = next.GetValueOrDefault(node);
            }
            foreach (var n in path)
                settled.Add(n);
        }
    }
}

namespace HaPlay.Core;

/// <summary>A bindable row in the cue-list workspace — a flattened <see cref="S.Media.Session.CueDefinition"/>.</summary>
public sealed record CueListItem(string Id, int Number, string Label)
{
    /// <summary>"3. Intro music" — the list display string.</summary>
    public string Display => $"{Number}. {Label}";
}

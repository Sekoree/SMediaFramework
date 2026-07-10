namespace S.Media.Core.Triggers;

/// <summary>
/// Tagged-union trigger argument - allocation-free for numeric OSC/MIDI control;
/// optional short text tail for addresses or note names.
/// </summary>
public readonly record struct TriggerPayload(
    TriggerValueKind Kind = TriggerValueKind.None,
    double NumericValue = 0,
    ReadOnlyMemory<char> TextValue = default)
{
    public static TriggerPayload None => default;
    public static TriggerPayload FromNumeric(double value) => new(TriggerValueKind.Numeric, value);
    public static TriggerPayload FromText(ReadOnlySpan<char> text) => new(TriggerValueKind.Text, 0, text.ToArray().AsMemory());
}

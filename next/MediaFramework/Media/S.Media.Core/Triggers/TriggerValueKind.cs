namespace S.Media.Core.Triggers;

/// <summary>Discriminant for <see cref="TriggerPayload"/>.</summary>
public enum TriggerValueKind
{
    None = 0,
    Numeric = 1,
    Text = 2,
}

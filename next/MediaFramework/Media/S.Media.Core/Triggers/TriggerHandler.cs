namespace S.Media.Core.Triggers;

/// <summary>Handler invoked by <see cref="TriggerBus.Fire"/>.</summary>
public delegate void TriggerHandler(in TriggerPayload payload);

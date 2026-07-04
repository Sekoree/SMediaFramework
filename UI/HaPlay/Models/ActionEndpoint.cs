using System.Text.Json.Serialization;

namespace HaPlay.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OSCActionEndpoint), typeDiscriminator: "osc")]
[JsonDerivedType(typeof(MIDIActionEndpoint), typeDiscriminator: "midi")]
public abstract record ActionEndpoint
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    [JsonIgnore]
    public virtual string KindLabel => "Endpoint";

    [JsonIgnore]
    public virtual string Summary => string.Empty;
}

public sealed record OSCActionEndpoint : ActionEndpoint
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 9000;

    [JsonIgnore]
    public override string KindLabel => "OSC";

    [JsonIgnore]
    public override string Summary => $"{Host}:{Port}";
}

public sealed record MIDIActionEndpoint : ActionEndpoint
{
    public int? DeviceId { get; init; }

    public string? DeviceName { get; init; }

    public int Channel { get; init; } = 0;

    [JsonIgnore]
    public override string KindLabel => "MIDI";

    [JsonIgnore]
    public override string Summary => $"{DeviceName ?? "(auto device)"} · ch {Channel + 1}";
}

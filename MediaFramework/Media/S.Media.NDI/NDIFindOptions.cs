namespace S.Media.NDI;

/// <summary>Options for <see cref="NDISource.Find"/>.</summary>
public sealed class NDIFindOptions
{
    public static NDIFindOptions Default { get; } = new();

    public bool ShowLocalSources { get; init; } = true;

    public string? Groups { get; init; }

    public string? ExtraIps { get; init; }
}

using S.Media.Core.Registry;

namespace S.Media.Source.Text;

/// <summary>
/// SESSION-02: the optional <c>text:</c> source module. Loading it registers <see cref="TextDecoderProvider"/> so
/// a host's <c>ShowSession</c> can render + play text cues the same way as any other clip — making a
/// <c>ShowDocument</c> that contains text cues portable across hosts (the pixels are produced by this module's
/// SkiaSharp renderer, not by host code). Opt-in, exactly like the NDI / YouTube / MMD source modules.
/// </summary>
public sealed class TextSourceModule : IMediaModule
{
    public string Name => "Text";

    public void Register(IMediaRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddDecoder(new TextDecoderProvider());
    }
}

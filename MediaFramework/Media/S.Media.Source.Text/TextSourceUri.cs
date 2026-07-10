using System.Text;
using System.Text.Json;

namespace S.Media.Source.Text;

/// <summary>
/// Encodes / decodes a <see cref="TextSourceSpec"/> as a <c>text:&lt;base64-json&gt;</c> URI. The whole render
/// spec travels through a <see cref="S.Media.Session"/> clip's single <c>MediaPath</c> string, so a text cue is a
/// normal source to the session - no special-casing in the show document. Source-generated JSON (AOT-safe).
/// </summary>
public static class TextSourceUri
{
    private const string Prefix = "text:";

    public static bool IsTextUri(string? uri) =>
        uri is not null && uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string Encode(TextSourceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var json = JsonSerializer.Serialize(spec, TextSourceJsonContext.Default.TextSourceSpec);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static TextSourceSpec? Decode(string uri)
    {
        if (!IsTextUri(uri))
            return null;
        try
        {
            var payload = Convert.FromBase64String(uri[Prefix.Length..]);
            return JsonSerializer.Deserialize(Encoding.UTF8.GetString(payload), TextSourceJsonContext.Default.TextSourceSpec);
        }
        catch
        {
            return null; // malformed URI → the provider surfaces a clear error via the null spec
        }
    }
}

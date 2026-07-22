using Avalonia.Input;

namespace HaPlay.Models;

/// <summary>Per-machine cue-player keyboard map. Empty gestures disable an action; malformed values are
/// rejected by the editor before they can be saved.</summary>
public sealed class CueHotkeyProfile
{
    public string Go { get; set; } = "Space";
    public string StopThenPanic { get; set; } = "Esc";
    public string PanicNow { get; set; } = "Ctrl+Esc";
    public string StandbySelected { get; set; } = "Enter";
    public string Back { get; set; } = "Backspace";
    public string Pause { get; set; } = "P";
    public string NextVisualizerPreset { get; set; } = "N";

    public CueHotkeyProfile Copy() => new()
    {
        Go = Go,
        StopThenPanic = StopThenPanic,
        PanicNow = PanicNow,
        StandbySelected = StandbySelected,
        Back = Back,
        Pause = Pause,
        NextVisualizerPreset = NextVisualizerPreset,
    };
}

/// <summary>Small, deterministic gesture parser shared by the editor and cue view. It accepts Avalonia key
/// enum names plus familiar aliases such as Esc, Backspace, Ctrl, Cmd and Win.</summary>
public static class CueHotkeyGesture
{
    public static bool Matches(string? text, KeyEventArgs e) =>
        TryParse(text, out var key, out var modifiers)
        && e.Key == key
        && e.KeyModifiers == modifiers;

    public static bool IsValid(string? text) =>
        string.IsNullOrWhiteSpace(text) || TryParse(text, out _, out _);

    public static bool TryParse(string? text, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None;
        modifiers = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                case "alt":
                case "option":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "meta":
                case "cmd":
                case "command":
                case "win":
                case "windows":
                    modifiers |= KeyModifiers.Meta;
                    break;
                default:
                    return false;
            }
        }

        var keyName = parts[^1].ToLowerInvariant() switch
        {
            "esc" => nameof(Key.Escape),
            "return" => nameof(Key.Enter),
            "backspace" => nameof(Key.Back),
            "del" => nameof(Key.Delete),
            "pgup" => nameof(Key.PageUp),
            "pgdn" => nameof(Key.PageDown),
            _ => parts[^1],
        };
        return Enum.TryParse(keyName, ignoreCase: true, out key)
               && key is not Key.None;
    }
}

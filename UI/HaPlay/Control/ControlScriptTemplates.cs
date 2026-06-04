namespace HaPlay.ControlGraph;

public sealed record ControlScriptTemplate(
    string Id,
    string DisplayName,
    string SuggestedPath,
    string Description,
    string Source);

public interface IControlScriptTemplateRepository
{
    IReadOnlyList<ControlScriptTemplate> Templates { get; }

    ControlScriptTemplate? FindById(string templateId);
}

public sealed class BuiltInControlScriptTemplateRepository : IControlScriptTemplateRepository
{
    public const string XTouchMiniX32FadersTemplateId = "xtouch-mini-x32-faders";
    public const string XTouchMiniX32MutesTemplateId = "xtouch-mini-x32-mutes";
    public const string X32LayerInitialRequestsTemplateId = "x32-layer-initial-requests";
    public const string XTouchMiniX32MuteFeedbackTemplateId = "xtouch-mini-x32-mute-feedback";

    public static BuiltInControlScriptTemplateRepository Instance { get; } = new();

    private readonly IReadOnlyList<ControlScriptTemplate> _templates;

    private BuiltInControlScriptTemplateRepository()
    {
        _templates =
        [
            new ControlScriptTemplate(
                XTouchMiniX32FadersTemplateId,
                "X-Touch Mini encoders -> X32 faders 1-8",
                "Scripts/xtouch-mini-x32-faders.mnd",
                "Maps X-Touch Mini MC-mode encoder CC16..CC23 to X32 channel faders 1..8 using relative speed values.",
                XTouchMiniX32FadersSource),
            new ControlScriptTemplate(
                XTouchMiniX32MutesTemplateId,
                "X-Touch Mini buttons -> X32 mutes 1-8",
                "Scripts/xtouch-mini-x32-mutes.mnd",
                "Maps X-Touch Mini MC-mode buttons 1..8 to X32 channel mute toggles using cached X32 state when available.",
                XTouchMiniX32MutesSource),
            new ControlScriptTemplate(
                X32LayerInitialRequestsTemplateId,
                "Layer enabled -> X32 initial value requests",
                "Scripts/x32-layer-initial-requests.mnd",
                "Requests X32 channel fader and mute values for channels 1..8 when a HaPlay layer becomes enabled.",
                X32LayerInitialRequestsSource),
            new ControlScriptTemplate(
                XTouchMiniX32MuteFeedbackTemplateId,
                "X32 mute cache -> X-Touch Mini button LEDs",
                "Scripts/xtouch-mini-x32-mute-feedback.mnd",
                "Updates X-Touch Mini MC-mode button LEDs 1..8 from X32 channel mute cache changes.",
                XTouchMiniX32MuteFeedbackSource),
        ];
    }

    public IReadOnlyList<ControlScriptTemplate> Templates => _templates;

    public ControlScriptTemplate? FindById(string templateId) =>
        _templates.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));

    public const string XTouchMiniX32FadersSource =
        """
        const defaultFaderValue = 0.75;
        const faderStep = 1.0 / 1023.0;

        fun encoderDeltaSteps(value) {
            if (value >= 1 && value <= 10)
                return value;

            if (value >= 65 && value <= 72)
                return -(value - 64);

            return 0;
        }

        export fun onXTouchFaderEncoder(event, context) {
            var controller = event.midi.controller;
            if (controller < 16 || controller > 23)
                return;

            var channel = controller - 15;
            var deltaSteps = encoderDeltaSteps(event.midi.value);
            if (deltaSteps == 0)
                return;

            var address = x32.channelFaderAddress(channel);
            var current = osc.cacheFloat("x32", address, defaultFaderValue);
            var next = math.clamp(current + deltaSteps * faderStep, 0.0, 1.0);

            osc.send("x32", address, osc.float32(next));
            osc.cacheSet("x32", address, next);
        }
        """;

    public const string XTouchMiniX32MutesSource =
        """
        const defaultOnValue = true;

        fun buttonToChannel(note) {
            if (note == 89) return 1;
            if (note == 90) return 2;
            if (note == 40) return 3;
            if (note == 41) return 4;
            if (note == 42) return 5;
            if (note == 43) return 6;
            if (note == 44) return 7;
            if (note == 45) return 8;
            return 0;
        }

        fun cachedOnState(deviceKey, address, fallback) {
            return osc.cacheFloat(deviceKey, address, fallback ? 1 : 0) != 0;
        }

        export fun onXTouchMuteButton(event, context) {
            if (!event.midi.isNoteOn || event.midi.velocity == 0)
                return;

            var channel = buttonToChannel(event.midi.note);
            if (channel == 0)
                return;

            var address = x32.channelMuteAddress(channel);
            var currentOn = cachedOnState("x32", address, defaultOnValue);
            var nextOn = !currentOn;

            osc.send("x32", address, osc.int32(nextOn ? 1 : 0));
            osc.cacheSet("x32", address, nextOn ? 1 : 0);
        }
        """;

    public const string X32LayerInitialRequestsSource =
        """
        export fun onX32LayerEnabled(event, context) {
            for (var channel = 1; channel <= 8; channel++) {
                osc.request("x32", x32.channelFaderAddress(channel));
                osc.request("x32", x32.channelMuteAddress(channel));
            }
        }
        """;

    public const string XTouchMiniX32MuteFeedbackSource =
        """
        fun buttonNoteForChannel(channel) {
            if (channel == 1) return 89;
            if (channel == 2) return 90;
            if (channel == 3) return 40;
            if (channel == 4) return 41;
            if (channel == 5) return 42;
            if (channel == 6) return 43;
            if (channel == 7) return 44;
            if (channel == 8) return 45;
            return 0;
        }

        fun channelFromMuteAddress(address) {
            if (address == x32.channelMuteAddress(1)) return 1;
            if (address == x32.channelMuteAddress(2)) return 2;
            if (address == x32.channelMuteAddress(3)) return 3;
            if (address == x32.channelMuteAddress(4)) return 4;
            if (address == x32.channelMuteAddress(5)) return 5;
            if (address == x32.channelMuteAddress(6)) return 6;
            if (address == x32.channelMuteAddress(7)) return 7;
            if (address == x32.channelMuteAddress(8)) return 8;
            return 0;
        }

        export fun onX32MuteCacheChanged(event, context) {
            var channel = channelFromMuteAddress(event.osc.address);
            if (channel == 0)
                return;

            var note = buttonNoteForChannel(channel);
            var isOn = event.value != 0;
            var ledVelocity = isOn ? 0 : 127;
            midi.sendNoteOn("xtouch", 1, note, ledVelocity);
        }
        """;
}

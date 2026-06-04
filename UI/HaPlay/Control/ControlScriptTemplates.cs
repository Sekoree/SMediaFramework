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
}

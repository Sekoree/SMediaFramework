using ProjectMLib;
using ProjectMLib.Runtime;
using S.Media.Audio.PortAudio;
using S.Media.Tools.NDIVisualizer;

// NDI Visualizer - captures a live audio input, feeds the chosen channels into projectM, and sends the
// visuals out as an NDI video source at a user-chosen resolution/framerate. Interactive setup wizard with
// save/load config; a live HUD shows NDI connection count, input level, and lets you ride the gain.
//
//   dotnet run --project MediaFramework/Tools/NDIVisualizer [-- [options]]
//     --config <path>   config file to load/save (default: ./ndi-visualizer.json)
//     --yes / -y        skip prompts; run the saved config as-is
//     --list            list host APIs + input devices and exit
//     --help / -h       show this help

var configPath = "ndi-visualizer.json";
var useYes = false;
var doList = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            configPath = args[++i];
            break;
        case "--yes" or "-y":
            useYes = true;
            break;
        case "--list":
            doList = true;
            break;
        case "--help" or "-h":
            PrintHelp();
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument '{args[i]}' (try --help)");
            return 2;
    }
}

if (doList)
{
    ListDevices();
    return 0;
}

if (!ProjectMRuntime.IsAvailable)
{
    Console.Error.WriteLine($"projectM is not available: {ProjectMRuntime.UnavailableReason}");
    Console.Error.WriteLine("Set MFP_PROJECTM_LIB to the directory containing libprojectM-4 if it is not on the default search path.");
    return 3;
}

var interactive = !Console.IsInputRedirected;
var loaded = VizConfig.TryLoad(configPath);

VizConfig cfg;
if (loaded is not null && (useYes || !interactive))
{
    Console.WriteLine($"Using saved config: {configPath}");
    cfg = loaded;
}
else if (loaded is not null && interactive)
{
    Console.WriteLine($"Found saved config: {configPath}");
    Console.WriteLine($"  device '{loaded.DeviceName}' via '{loaded.HostApiName}', " +
                      $"NDI '{loaded.NDIName}', {loaded.Width}x{loaded.Height} @ {loaded.Fps} fps");
    cfg = ConsolePrompt.Confirm("Run this config as-is?", defaultYes: true)
        ? loaded
        : RunWizard(loaded, configPath);
}
else if (interactive)
{
    cfg = RunWizard(null, configPath);
}
else
{
    Console.Error.WriteLine($"no config at '{configPath}' and stdin is not interactive. " +
                            "Run once interactively to create a config, or pass --config <path>.");
    return 2;
}

VizToNDIPipeline.Parameters parameters;
try
{
    parameters = ResolveParameters(cfg);
}
catch (Exception ex) when (ex is InvalidOperationException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 4;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let us tear down cleanly instead of a hard exit
    // ReSharper disable once AccessToDisposedClosure
    cts.Cancel();
};

// 's' at runtime persists the (possibly gain-adjusted) config back to the same path.
var pipeline = new VizToNDIPipeline(parameters, onSaveRequested: gainDb =>
{
    try
    {
        (cfg with { GainDb = gainDb }).Save(configPath);
        Console.WriteLine($"\n  saved config → {configPath} (gain {gainDb:+0;-0;0} dB)");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine($"\n  could not save config: {ex.Message}");
    }
});

try
{
    pipeline.Run(cts.Token);
    return 0;
}
catch (Exception ex) when (ex is InvalidOperationException or PortAudioException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

// ---------------------------------------------------------------------------------------------------

VizConfig RunWizard(VizConfig? defaults, string savePath)
{
    var d = defaults ?? new VizConfig();

    var allInputs = PortAudioDeviceCatalog.EnumerateInputDevices();
    if (allInputs.Count == 0)
        throw new InvalidOperationException("no input-capable audio devices were found.");

    var hostApis = PortAudioDeviceCatalog.EnumerateHostApis();
    var inputApiIndices = allInputs.Select(x => x.HostApiIndex).ToHashSet();
    var selectableApis = hostApis.Where(a => inputApiIndices.Contains(a.Index)).ToList();

    // --- Host API ---
    var defApiIdx = IndexOfOrDefault(selectableApis, a => a.Name.Equals(d.HostApiName, StringComparison.OrdinalIgnoreCase));
    if (defApiIdx < 0)
    {
        var defaultInputApi = allInputs.FirstOrDefault(x => x.IsDefault).HostApiIndex;
        defApiIdx = Math.Max(0, IndexOfOrDefault(selectableApis, a => a.Index == defaultInputApi));
    }

    var apiPick = selectableApis[ConsolePrompt.SelectIndex(
        "Select audio host API:",
        selectableApis,
        a => $"{a.Name}  ({allInputs.Count(x => x.HostApiIndex == a.Index)} input device(s))",
        defApiIdx)];

    // --- Device ---
    var devices = PortAudioDeviceCatalog.EnumerateInputDevices(apiPick.Index);
    var defDevIdx = IndexOfOrDefault(devices, x => x.Name.Equals(d.DeviceName, StringComparison.OrdinalIgnoreCase));
    if (defDevIdx < 0)
        defDevIdx = Math.Max(0, IndexOfOrDefault(devices, x => x.IsDefault));

    var devPick = devices[ConsolePrompt.SelectIndex(
        "Select input device:",
        devices,
        x => $"{x.Name}  ({x.MaxInputChannels} ch, {x.DefaultSampleRate:0} Hz){(x.IsDefault ? " [default]" : "")}",
        defDevIdx)];

    // --- Channels (device opens at max channel count; pick which to visualize) ---
    var channels = ConsolePrompt.ReadChannels(
        "Channels to visualize", devPick.MaxInputChannels, d.Channels);

    // --- Preset directory (default to the built preset pack when the user hasn't set one) ---
    var presetDefault = d.PresetDirectory ?? "";
    if (presetDefault.Length == 0 && ProjectMLibraryResolver.TryFindDevBuildRoot() is { } devRoot)
    {
        var packed = Path.Combine(devRoot, "presets");
        if (Directory.Exists(packed))
            presetDefault = packed;
    }

    var presetDir = ConsolePrompt.ReadString("projectM preset directory (blank = idle preset)", presetDefault);
    if (presetDir.Length > 0 && !Directory.Exists(presetDir))
        Console.WriteLine($"  note: '{presetDir}' does not exist yet - projectM will fall back to its idle preset.");

    // --- Resolution / framerate / NDI name ---
    var (w, h) = ConsolePrompt.ReadResolution("Output resolution (WxH)", d.Width, d.Height);
    var fps = ConsolePrompt.ReadInt("Framerate (fps)", d.Fps, 1, 240);
    var ndiName = ConsolePrompt.ReadString("NDI output name", d.NDIName);

    var result = d with
    {
        HostApiName = apiPick.Name,
        DeviceName = devPick.Name,
        Channels = channels,
        PresetDirectory = presetDir.Length == 0 ? null : presetDir,
        Width = w,
        Height = h,
        Fps = fps,
        NDIName = ndiName,
        SampleRate = 0, // follow the device's default rate
    };

    if (ConsolePrompt.Confirm($"Save this configuration to '{savePath}'?", defaultYes: true))
    {
        try
        {
            result.Save(savePath);
            Console.WriteLine($"  saved → {savePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  could not save config: {ex.Message}");
        }
    }

    return result;
}

static VizToNDIPipeline.Parameters ResolveParameters(VizConfig cfg)
{
    var hostApis = PortAudioDeviceCatalog.EnumerateHostApis();
    var api = hostApis.FirstOrDefault(a => a.Name.Equals(cfg.HostApiName, StringComparison.OrdinalIgnoreCase));
    int? apiIndex = api.Name is null ? null : api.Index;
    if (cfg.HostApiName.Length > 0 && apiIndex is null)
        Console.WriteLine($"note: host API '{cfg.HostApiName}' not present; searching all host APIs for the device.");

    var devices = PortAudioDeviceCatalog.EnumerateInputDevices(apiIndex);
    if (devices.Count == 0 && apiIndex is not null)
        devices = PortAudioDeviceCatalog.EnumerateInputDevices();

    var dev = devices.FirstOrDefault(x => x.Name.Equals(cfg.DeviceName, StringComparison.OrdinalIgnoreCase));
    if (dev.Name is null)
        dev = devices.FirstOrDefault(x => x.Name.Contains(cfg.DeviceName, StringComparison.OrdinalIgnoreCase));
    if (dev.Name is null)
        dev = devices.FirstOrDefault(x => x.IsDefault);
    if (dev.Name is null && devices.Count > 0)
        dev = devices[0];
    if (dev.Name is null)
        throw new InvalidOperationException("no input-capable audio device is available.");

    if (!dev.Name.Equals(cfg.DeviceName, StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"note: device '{cfg.DeviceName}' not found; using '{dev.Name}' instead.");

    var maxChannels = Math.Max(1, dev.MaxInputChannels);
    var sampleRate = cfg.SampleRate > 0 ? cfg.SampleRate : (int)Math.Round(dev.DefaultSampleRate);
    if (sampleRate <= 0)
        sampleRate = 48_000;

    var channels0 = cfg.Channels
        .Where(c => c >= 1 && c <= maxChannels)
        .Select(c => c - 1)
        .Distinct()
        .ToArray();
    if (channels0.Length == 0)
        channels0 = Enumerable.Range(0, maxChannels).ToArray();

    return new VizToNDIPipeline.Parameters(
        dev.GlobalDeviceIndex,
        dev.Name,
        maxChannels,
        channels0,
        sampleRate,
        cfg.Width,
        cfg.Height,
        cfg.Fps,
        cfg.NDIName,
        cfg.PresetDirectory,
        cfg.PresetDurationSeconds,
        cfg.Shuffle,
        cfg.GainDb);
}

static void ListDevices()
{
    var hostApis = PortAudioDeviceCatalog.EnumerateHostApis();
    var inputs = PortAudioDeviceCatalog.EnumerateInputDevices();
    Console.WriteLine("Host APIs and input devices:");
    foreach (var api in hostApis)
    {
        var apiDevices = inputs.Where(d => d.HostApiIndex == api.Index).ToList();
        if (apiDevices.Count == 0)
            continue;
        Console.WriteLine($"  {api.Name}");
        foreach (var d in apiDevices)
            Console.WriteLine(
                $"    #{d.GlobalDeviceIndex,-3} {d.Name}  ({d.MaxInputChannels} ch, {d.DefaultSampleRate:0} Hz){(d.IsDefault ? " [default]" : "")}");
    }
}

static int IndexOfOrDefault<T>(IReadOnlyList<T> items, Func<T, bool> match)
{
    for (var i = 0; i < items.Count; i++)
        if (match(items[i]))
            return i;
    return -1;
}

static void PrintHelp()
{
    Console.WriteLine("NDI Visualizer - live audio → projectM → NDI");
    Console.WriteLine();
    Console.WriteLine("Usage: NDIVisualizer [options]");
    Console.WriteLine("  --config <path>   config file to load/save (default: ./ndi-visualizer.json)");
    Console.WriteLine("  --yes, -y         skip prompts; run the saved config as-is");
    Console.WriteLine("  --list            list host APIs + input devices and exit");
    Console.WriteLine("  --help, -h        show this help");
    Console.WriteLine();
    Console.WriteLine("Runtime keys: [+/-] or [Up/Down] gain   [n] next preset   [s] save config   [q] quit");
}

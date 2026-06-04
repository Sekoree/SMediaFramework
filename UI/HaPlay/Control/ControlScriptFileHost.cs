using Mond;
using Mond.Debugger;
using Mond.Libraries;

namespace HaPlay.ControlGraph;

public interface IControlScriptSourceProvider
{
    bool TryReadScript(string scriptPath, out string source);
}

public sealed class InMemoryControlScriptSourceProvider : IControlScriptSourceProvider
{
    private readonly Dictionary<string, string> _sources;

    public InMemoryControlScriptSourceProvider(IReadOnlyDictionary<string, string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToDictionary(kvp => ControlScriptPath.Normalize(kvp.Key), kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    public bool TryReadScript(string scriptPath, out string source) =>
        _sources.TryGetValue(ControlScriptPath.Normalize(scriptPath), out source!);
}

public sealed class FileSystemControlScriptSourceProvider : IControlScriptSourceProvider
{
    private readonly string _rootDirectory;

    public FileSystemControlScriptSourceProvider(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Root directory is required.", nameof(rootDirectory));

        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public bool TryReadScript(string scriptPath, out string source)
    {
        source = string.Empty;

        var normalizedPath = ControlScriptPath.Normalize(scriptPath);
        if (!ControlScriptPath.IsSafeProjectPath(normalizedPath))
            return false;

        var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, normalizedPath));
        if (!IsUnderRoot(fullPath))
            return false;

        if (!File.Exists(fullPath))
            return false;

        source = File.ReadAllText(fullPath);
        return true;
    }

    private bool IsUnderRoot(string fullPath)
    {
        if (string.Equals(fullPath, _rootDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        var root = _rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _rootDirectory
            : _rootDirectory + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ControlScriptFileHost
{
    public const int DefaultInstructionLimit = 100_000;

    private const string HostEntryPath = "haplay-script-host.mnd";

    private readonly IControlScriptSourceProvider _sourceProvider;
    private readonly int _instructionLimit;

    public ControlScriptFileHost(
        IControlScriptSourceProvider sourceProvider,
        int instructionLimit = DefaultInstructionLimit,
        ControlScriptRuntimeServices? runtimeServices = null)
    {
        _sourceProvider = sourceProvider ?? throw new ArgumentNullException(nameof(sourceProvider));
        _instructionLimit = instructionLimit <= 0 ? DefaultInstructionLimit : instructionLimit;
        RuntimeServices = runtimeServices ?? new ControlScriptRuntimeServices();
    }

    public ControlScriptRuntimeServices RuntimeServices { get; }

    public ControlScriptModule LoadModule(string scriptPath)
    {
        var normalizedPath = ControlScriptPath.Normalize(scriptPath);
        if (!ControlScriptPath.IsSafeProjectPath(normalizedPath))
            throw new ControlScriptException($"Script path is not a safe project-relative path: {scriptPath}");

        var state = CreateState();
        ConfigureRequireLibrary(state);

        var exports = state.Run($"return require('{EscapeMondString(normalizedPath)}');", HostEntryPath);
        if (exports.Type != MondValueType.Object)
            throw new ControlScriptException($"Script '{normalizedPath}' did not export a module object.");

        return new ControlScriptModule(normalizedPath, state, exports);
    }

    public MondValue Invoke(string scriptPath, string exportedFunctionName, params MondValue[] arguments)
    {
        var module = LoadModule(scriptPath);
        return module.Invoke(exportedFunctionName, arguments);
    }

    private MondState CreateState()
    {
        var libraries = new MondLibraryManager();
        libraries.Add(new CoreLibraries());
        libraries.Add(new ControlScriptApiLibrary(RuntimeServices));

        return new MondState
        {
            Libraries = libraries,
            Options = new MondCompilerOptions
            {
                DebugInfo = MondDebugInfoLevel.Full,
                MakeRootDeclarationsGlobal = false,
                UseImplicitGlobals = true,
            },
            Debugger = new InstructionLimitDebugger(_instructionLimit),
        };
    }

    private void ConfigureRequireLibrary(MondState state)
    {
        state.Libraries.Configure(libraries =>
        {
            var require = libraries.Get<RequireLibrary>()
                ?? throw new ControlScriptException("Mond require library is not available.");

            require.Resolver = ResolveModule;
            require.Loader = LoadResolvedModule;
        });
    }

    private string ResolveModule(string moduleName, IEnumerable<string> searchDirectories)
    {
        foreach (var candidate in ControlScriptPath.GetCandidatePaths(moduleName, searchDirectories))
        {
            if (_sourceProvider.TryReadScript(candidate, out _))
                return candidate;
        }

        throw new MondRuntimeException("require: module could not be found: {0}", moduleName);
    }

    private string LoadResolvedModule(string resolvedPath)
    {
        if (_sourceProvider.TryReadScript(resolvedPath, out var source))
            return source;

        throw new MondRuntimeException("require: module could not be loaded: {0}", resolvedPath);
    }

    private static string EscapeMondString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
}

public sealed class ControlScriptModule
{
    public ControlScriptModule(string scriptPath, MondState state, MondValue exports)
    {
        if (exports.Type != MondValueType.Object)
            throw new ArgumentException("Exports must be a Mond object.", nameof(exports));

        ScriptPath = scriptPath;
        State = state ?? throw new ArgumentNullException(nameof(state));
        Exports = exports;
    }

    public string ScriptPath { get; }

    public MondState State { get; }

    public MondValue Exports { get; }

    public IReadOnlyList<string> ExportedFunctionNames =>
        Exports.AsDictionary
            .Where(kvp => kvp.Key.Type == MondValueType.String && kvp.Value.Type == MondValueType.Function)
            .Select(kvp => (string)kvp.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public bool TryGetExportedFunction(string functionName, out MondValue function)
    {
        function = string.IsNullOrWhiteSpace(functionName)
            ? MondValue.Undefined
            : Exports[functionName];

        return function.Type == MondValueType.Function;
    }

    public MondValue Invoke(string exportedFunctionName, params MondValue[] arguments)
    {
        if (!TryGetExportedFunction(exportedFunctionName, out var function))
            throw new ControlScriptException($"Script '{ScriptPath}' does not export function '{exportedFunctionName}'.");

        return State.Call(function, arguments ?? []);
    }
}

internal static class ControlScriptPath
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];

        return normalized;
    }

    public static bool IsSafeProjectPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = Normalize(path);
        if (Path.IsPathRooted(normalized) || normalized.StartsWith("/", StringComparison.Ordinal))
            return false;

        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .All(segment => segment is not "." and not "..");
    }

    public static IEnumerable<string> GetCandidatePaths(string moduleName, IEnumerable<string> searchDirectories)
    {
        var normalizedModule = Normalize(moduleName);
        if (!IsSafeProjectPath(normalizedModule))
            yield break;

        foreach (var candidate in AppendExtension(normalizedModule))
            yield return candidate;

        foreach (var directory in searchDirectories.Select(Normalize))
        {
            if (string.IsNullOrWhiteSpace(directory) || directory == ".")
                continue;

            if (!IsSafeProjectPath(directory))
                continue;

            foreach (var candidate in AppendExtension(Normalize($"{directory}/{normalizedModule}")))
                yield return candidate;
        }
    }

    private static IEnumerable<string> AppendExtension(string path)
    {
        yield return path;

        if (!path.EndsWith(".mnd", StringComparison.OrdinalIgnoreCase))
            yield return path + ".mnd";
    }
}

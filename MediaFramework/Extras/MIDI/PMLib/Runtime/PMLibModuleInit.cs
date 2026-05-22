using System.Runtime.CompilerServices;

namespace PMLib.Runtime;

/// <summary>
/// Automatically registers the PortMidi native library resolver before any
/// <c>Native.*</c> P/Invoke call can fire.
/// </summary>
internal static class PMLibModuleInit
{
    // [ModuleInitializer] runs exactly once, before any other code in this assembly
    // (including static constructors), so Native.* calls are always safe after this point.
    // CA2255 is suppressed: using [ModuleInitializer] in a library for DllImportResolver
    // registration is an intentional, established pattern.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize() => PortMidiLibraryResolver.Install();
#pragma warning restore CA2255
}


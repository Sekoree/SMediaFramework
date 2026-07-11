using System.Runtime.CompilerServices;

namespace ProjectMLib.Runtime;

/// <summary>Registers the projectM library resolver before any <c>Native.*</c> P/Invoke can fire
/// (same intentional [ModuleInitializer] pattern as NDILib).</summary>
internal static class ProjectMLibModuleInit
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize() => ProjectMLibraryResolver.Install();
#pragma warning restore CA2255
}

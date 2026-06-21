using System.Runtime.CompilerServices;

namespace S.Media.MiniAudio.Runtime;

internal static class MiniAudioModuleInit
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize() => MiniAudioLibraryResolver.Install();
#pragma warning restore CA2255
}

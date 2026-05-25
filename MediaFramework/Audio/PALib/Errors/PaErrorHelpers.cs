using PALib.Types.Core;

namespace PALib.Errors;

internal static class PaErrorHelpers
{
    public static string Describe(PaError error)
        => Native.Pa_GetErrorText(error) ?? error.ToString();

    public static PaHostErrorInfo? GetLastHostErrorInfo() => Native.Pa_GetLastHostErrorInfo();

    public static string GetLastHostErrorTextSafe()
        => Native.Pa_GetLastHostErrorInfo()?.ErrorText ?? string.Empty;
}

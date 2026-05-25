namespace S.Media.NDI;

public sealed class NDIException : Exception
{
    public int ErrorCode { get; }

    internal NDIException(int code, string message) : base($"{message} (NDI error {code})")
    {
        ErrorCode = code;
    }
}

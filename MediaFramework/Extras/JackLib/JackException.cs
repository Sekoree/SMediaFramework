namespace JackLib;

/// <summary>Exception thrown when a JACK API operation fails.</summary>
public sealed class JackException : Exception
{
    public JackException(string message) : base(message) { }
    public JackException(string message, Exception inner) : base(message, inner) { }
}


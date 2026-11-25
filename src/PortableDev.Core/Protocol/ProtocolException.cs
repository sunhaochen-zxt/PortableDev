namespace PortableDev.Core.Protocol;

/// <summary>A protocol-level failure that must be reported as `ERR_PROTOCOL`.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message, string code = ErrorCodes.Protocol, bool retryable = false)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }

    /// <summary>Whether this failure should close the connection.</summary>
    public bool Fatal { get; init; } = true;
}

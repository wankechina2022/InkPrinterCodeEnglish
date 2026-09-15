namespace DominoA200Sdk.Exceptions;

/// <summary>
/// Thrown when the printer fails to answer a command within the configured window.
///
/// <para>
/// A timeout is distinct from a rejection: the bytes were written to the socket but
/// no <c>0x06</c> or <c>0x15</c> ever came back. On a TCP link this usually means the
/// peer has stopped responding, so the client treats it as a liveness failure and
/// begins reconnecting.
/// </para>
/// </summary>
public sealed class PrinterTimeoutException : Exception
{
    /// <summary>Creates the exception with the elapsed wait described.</summary>
    /// <param name="message">Human-readable description of the timed-out operation.</param>
    public PrinterTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the elapsed wait described, wrapping an inner cause.</summary>
    /// <param name="message">Human-readable description of the timed-out operation.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public PrinterTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

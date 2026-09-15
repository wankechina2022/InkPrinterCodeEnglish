namespace DominoA200Sdk.Exceptions;

/// <summary>
/// Thrown when the printer rejects a command with the negative acknowledgement
/// byte <c>0x15</c> (NAK).
///
/// <para>
/// The most common cause on an A200+ is a full on-board FIFO queue: the printer
/// accepts at most three jobs before it starts refusing new ones.
/// </para>
/// </summary>
public sealed class PrinterNackException : Exception
{
    /// <summary>Creates the exception for a rejected command.</summary>
    /// <param name="message">Human-readable description of what was rejected.</param>
    public PrinterNackException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception for a rejected command, wrapping an inner cause.</summary>
    /// <param name="message">Human-readable description of what was rejected.</param>
    /// <param name="innerException">The underlying cause, if any.</param>
    public PrinterNackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

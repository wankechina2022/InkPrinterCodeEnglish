namespace DominoA200Sdk.Models;

/// <summary>
/// Payload for <see cref="DominoA200Client.OnJobCompleted"/>.
///
/// <para>
/// The A200+ pushes a bare <c>0x32</c> byte when a job finishes printing; it does not
/// echo an identifier. The SDK therefore correlates the event with the oldest job in
/// its own FIFO mirror and reports that job's id here.
/// </para>
/// </summary>
public sealed class PrinterEventArgs : EventArgs
{
    /// <summary>Creates the event payload.</summary>
    public PrinterEventArgs(string jobId, string codeValue, DateTime completedAt)
    {
        JobId = jobId ?? string.Empty;
        CodeValue = codeValue ?? string.Empty;
        CompletedAt = completedAt;
    }

    /// <summary>Identifier of the job that completed, when it could be correlated.</summary>
    public string JobId { get; }

    /// <summary>Code text of the completed job, when it could be correlated.</summary>
    public string CodeValue { get; }

    /// <summary>Local time at which the completion event was observed.</summary>
    public DateTime CompletedAt { get; }

    /// <summary>Short diagnostic description.</summary>
    public override string ToString()
    {
        return "JobCompleted[JobId=" + JobId + ", Code=" + CodeValue + "]";
    }
}

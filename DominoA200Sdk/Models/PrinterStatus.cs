namespace DominoA200Sdk.Models;

/// <summary>
/// A snapshot of printer health returned by status queries.
/// </summary>
public sealed class PrinterStatus
{
    /// <summary>Creates a status snapshot.</summary>
    public PrinterStatus(bool isConnected, int fifoQueueCount, string stateText)
    {
        IsConnected = isConnected;
        FifoQueueCount = fifoQueueCount;
        StateText = stateText ?? string.Empty;
    }

    /// <summary>Whether a transport connection is currently established.</summary>
    public bool IsConnected { get; }

    /// <summary>Jobs known to be queued on the printer.</summary>
    public int FifoQueueCount { get; }

    /// <summary>Human-readable state label.</summary>
    public string StateText { get; }

    /// <summary>Short diagnostic description.</summary>
    public override string ToString()
    {
        return "PrinterStatus[Connected=" + IsConnected.ToString()
               + ", Fifo=" + FifoQueueCount.ToString()
               + ", State=" + StateText + "]";
    }
}

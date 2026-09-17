namespace DominoA200Sdk.Core;

/// <summary>
/// High-level operational state of a connected printer.
/// </summary>
public enum PrinterState
{
    /// <summary>No transport connection is established.</summary>
    Disconnected = 0,

    /// <summary>Transport is up and the printer is idle, ready to accept jobs.</summary>
    Idle = 1,

    /// <summary>A job has been accepted and is being printed.</summary>
    Printing = 2,

    /// <summary>The printer reported a fault or is refusing jobs.</summary>
    Alarm = 3
}

/// <summary>
/// Tracks the printer's operational state and raises a notification whenever it
/// transitions.
///
/// <para>
/// <b>Design.</b> The state machine is deliberately passive: it does not own the
/// socket or the timers. The client pushes observations in (a connection came up, a
/// job was acknowledged, an event arrived) and the machine decides the resulting
/// state, then fires <see cref="StateChanged"/>.
/// </para>
///
/// <para>
/// <b>Thread safety.</b> All transitions are guarded by an internal monitor; the
/// event is raised outside the lock so a handler cannot deadlock the caller.
/// </para>
/// </summary>
public sealed class PrinterStateMachine
{
    private readonly object _sync = new object();
    private PrinterState _state = PrinterState.Disconnected;

    /// <summary>
    /// Raised after every accepted transition, carrying the previous and the new state.
    /// Handlers run on the thread that caused the transition.
    /// </summary>
    public event EventHandler<PrinterStateChangedEventArgs>? StateChanged;

    /// <summary>The current state. Reading is atomic for enum-sized values.</summary>
    public PrinterState State
    {
        get { return _state; }
    }

    /// <summary>
    /// Drive the machine to <paramref name="target"/>.
    /// <para>
    /// A transition to the state already held is a no-op and raises nothing; this
    /// keeps repeated observations from flooding subscribers.
    /// </para>
    /// </summary>
    /// <param name="target">The desired new state.</param>
    public void TransitionTo(PrinterState target)
    {
        PrinterState previous;
        bool changed;

        lock (_sync)
        {
            previous = _state;
            changed = previous != target;
            _state = target;
        }

        if (changed)
        {
            // Raised outside the lock: a slow or re-entrant handler must not block
            // other threads from reading the state.
            StateChanged?.Invoke(this, new PrinterStateChangedEventArgs(previous, target));
        }
    }

    /// <summary>
    /// Convenience wrapper: move to <see cref="PrinterState.Alarm"/> only when the
    /// printer is not already in a terminal disconnected state.
    /// </summary>
    public void ReportAlarm()
    {
        PrinterState current = _state;
        if (current != PrinterState.Disconnected)
        {
            TransitionTo(PrinterState.Alarm);
        }
    }
}

/// <summary>
/// Payload for <see cref="PrinterStateMachine.StateChanged"/>.
/// </summary>
public sealed class PrinterStateChangedEventArgs : EventArgs
{
    /// <summary>Creates the event payload.</summary>
    public PrinterStateChangedEventArgs(PrinterState previous, PrinterState current)
    {
        Previous = previous;
        Current = current;
    }

    /// <summary>The state held before the transition.</summary>
    public PrinterState Previous { get; }

    /// <summary>The state held after the transition.</summary>
    public PrinterState Current { get; }
}

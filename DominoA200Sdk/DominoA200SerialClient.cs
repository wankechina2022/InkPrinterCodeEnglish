using System.IO.Ports;
using DominoA200Sdk.Core;
using DominoA200Sdk.Exceptions;
using DominoA200Sdk.Models;

namespace DominoA200Sdk;

/// <summary>
/// Talks to a Domino A200+ printer over an <b>RS232 serial link</b> using the same
/// Codenet protocol as <see cref="DominoA200Client"/>.
///
/// <para>
/// <b>One protocol, two transports.</b> This class reuses the exact same framing
/// (<see cref="CodenetFrame"/>), FIFO mirror (<see cref="JobQueue"/>) and state machine
/// (<see cref="PrinterStateMachine"/>) as the TCP client - only the byte transport
/// differs. The public surface mirrors <see cref="DominoA200Client"/> so swapping a
/// printer from TCP to serial is a one-line constructor change.
/// </para>
///
/// <para>
/// <b>Serial parameters.</b> The link is opened 8 data bits / 1 stop bit / no parity
/// (<c>8N1</c>) at the configured baud rate (default 9600). These match the
/// field-tested serial connection in the InkPrinterCode host application.
/// </para>
///
/// <para>
/// <b>Threading.</b> One background receive thread owns all reads; public methods are
/// safe to call from multiple threads. <see cref="OnJobCompleted"/> fires on the
/// receive thread, so a handler must marshal to the UI thread before touching UI.
/// </para>
/// </summary>
/// <remarks>
/// [2026-09-17] Added to give the SDK a native RS232 transport. The serial port is
/// guarded by <c>_ioLock</c> exactly like the host's SerialPrinterConnection: the
/// receive thread's read and the caller's write never touch the SerialPort object
/// concurrently. A short read timeout keeps the loop responsive to disconnect and to
/// <see cref="DisconnectAsync"/>.
/// </remarks>
public sealed class DominoA200SerialClient : IDisposable
{
    // ============================================================
    // Configuration
    // ============================================================

    private readonly string _portName;
    private readonly int _baudRate;
    private readonly int _responseTimeoutMs;
    private readonly int _reconnectDelayMs;
    private readonly bool _autoReconnect;

    // Polling read window for the receive loop (ms). Short enough that the loop wakes
    // promptly when _running flips or the port is closed from another thread.
    private const int SerialPollMs = 250;

    // ============================================================
    // Transport and protocol state
    // ============================================================

    private SerialPort? _port;
    private readonly object _ioLock = new object();
    private readonly object _connectLock = new object();

    private Thread? _receiveThread;
    private volatile bool _running;
    private volatile bool _connected;
    private volatile bool _disposed;
    private volatile bool _connecting;
    private int _reconnecting;

    private readonly List<byte> _rxBuffer = new List<byte>(256);
    private const int MaxRxBufferBytes = 64 * 1024;
    private readonly object _rxLock = new object();

    private readonly PrinterStateMachine _stateMachine = new PrinterStateMachine();
    private readonly JobQueue _fifoMirror = new JobQueue();

    private readonly object _ackLock = new object();
    private bool _ackReceived;
    private bool _ackValue;

    private readonly SemaphoreSlim _commandLock = new SemaphoreSlim(1, 1);
    private long _jobSequence;

    /// <summary>Optional sink for raw hexadecimal traffic. Set to null to disable.</summary>
    public Action<string>? TrafficLogger { get; set; }

    // ============================================================
    // Public events
    // ============================================================

    /// <summary>Raised when the printer pushes a <c>0x32</c> print-complete event.</summary>
    public event EventHandler<PrinterEventArgs>? OnJobCompleted;

    /// <summary>Raised whenever the serial connection is established.</summary>
    public event EventHandler? OnConnected;

    /// <summary>Raised whenever the serial connection is lost or closed.</summary>
    public event EventHandler? OnDisconnected;

    /// <summary>Raised for every state-machine transition.</summary>
    public event EventHandler<PrinterStateChangedEventArgs>? OnStateChanged;

    /// <summary>Raised when a receive failure is detected and a reconnect is about to start.</summary>
    public event EventHandler? OnReconnecting;

    // ============================================================
    // Construction
    // ============================================================

    /// <summary>
    /// Create a client bound to a serial port. No connection is made until
    /// <see cref="ConnectAsync"/> is called.
    /// </summary>
    /// <param name="portName">Serial port name (e.g. <c>COM3</c> or <c>/dev/ttyS0</c>).</param>
    /// <param name="baudRate">RS232 baud rate. Default 9600.</param>
    /// <param name="responseTimeoutMs">How long to wait for an ACK/NAK before treating the command as timed out. Default 3000 ms.</param>
    /// <param name="connectTimeoutMs">Reserved for parity with the TCP client; serial <c>Open()</c> is near-instant.</param>
    /// <param name="autoReconnect">When true, a lost link triggers a background reconnect loop. Default false.</param>
    /// <param name="reconnectDelayMs">Delay between reconnect attempts. Default 2000 ms.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="portName"/> is null or whitespace.</exception>
    public DominoA200SerialClient(
        string portName,
        int baudRate = 9600,
        int responseTimeoutMs = 3000,
        int connectTimeoutMs = 5000,
        bool autoReconnect = false,
        int reconnectDelayMs = 2000)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Serial port name must not be null or empty.", nameof(portName));
        }

        _portName = portName.Trim();
        _baudRate = baudRate > 0 ? baudRate : 9600;
        _responseTimeoutMs = responseTimeoutMs > 0 ? responseTimeoutMs : 3000;
        // connectTimeoutMs is accepted only to keep the constructor signature swappable with
        // DominoA200Client; SerialPort.Open() is synchronous and has no timeout to apply.
        _autoReconnect = autoReconnect;
        _reconnectDelayMs = reconnectDelayMs > 0 ? reconnectDelayMs : 2000;

        _stateMachine.StateChanged += (sender, args) => OnStateChanged?.Invoke(this, args);
    }

    // ============================================================
    // Public surface
    // ============================================================

    /// <summary>Whether the serial link is currently open.</summary>
    public bool IsConnected => _connected;

    /// <summary>The current high-level printer state.</summary>
    public PrinterState State => _stateMachine.State;

    /// <summary>Serial port name the client is configured to use.</summary>
    public string PortName => _portName;

    /// <summary>Baud rate the client is configured to use.</summary>
    public int BaudRate => _baudRate;

    /// <summary>
    /// Open the serial port, start the receive loop and run the protocol handshake
    /// (print-signal setup plus queue clear).
    /// </summary>
    /// <param name="cancellationToken">Cancels the connect attempt.</param>
    /// <exception cref="UnauthorizedAccessException">Thrown when the port is already in use.</exception>
    /// <exception cref="System.IO.IOException">Thrown when the port does not exist or cannot be opened.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        bool lockTaken = false;
        try
        {
            System.Threading.Monitor.Enter(_connectLock, ref lockTaken);
            if (_connected || _connecting)
            {
                return;
            }
            _connecting = true;
        }
        finally
        {
            if (lockTaken)
            {
                System.Threading.Monitor.Exit(_connectLock);
            }
        }

        try
        {
            // [2026-09-17] Open the RS232 link 8N1. Open() throws (port busy / missing)
            // and the caller's catch handles it - there is no socket timeout to race here.
            SerialPort port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One);
            port.WriteTimeout = 5000;

            try
            {
                port.Open();
            }
            catch
            {
                port.Dispose();
                throw;
            }

            lock (_ioLock)
            {
                _port = port;
            }

            _running = true;
            _connected = true;
            _stateMachine.TransitionTo(PrinterState.Idle);

            _receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "DominoA200.Serial"
            };
            _receiveThread.Start();

            OnConnected?.Invoke(this, EventArgs.Empty);
            LogTraffic("CONNECT", _portName + " @ " + _baudRate.ToString() + " 8N1");

            // Protocol handshake: tell the printer to report print completion, then clear
            // any stale queued data so the FIFO mirror starts from a known state.
            await SendWithAckAsync(CodenetFrame.BuildSignalSetupFrame(), "print signal setup", cancellationToken)
                .ConfigureAwait(false);
            await SendWithAckAsync(CodenetFrame.BuildClearQueueFrame(0), "clear TCP/IP queue", cancellationToken)
                .ConfigureAwait(false);
            await SendWithAckAsync(CodenetFrame.BuildClearQueueFrame(1), "clear RS232 queue", cancellationToken)
                .ConfigureAwait(false);
            await SendWithAckAsync(CodenetFrame.BuildClearQueueFrame(2), "clear history queue", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _connecting = false;
        }
    }

    /// <summary>
    /// Submit a print job over the serial link. The method writes the job frame and
    /// waits for the acknowledgement; a <c>0x06</c> means acceptance (job added to the
    /// FIFO mirror), a <c>0x15</c> raises <see cref="PrinterNackException"/> (queue full).
    /// </summary>
    /// <param name="job">The job to print.</param>
    /// <param name="cancellationToken">Cancels the wait for acknowledgement.</param>
    /// <returns>The job identifier assigned by the SDK.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="job"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the client is not connected.</exception>
    /// <exception cref="PrinterNackException">Thrown when the printer rejects the job with <c>0x15</c>.</exception>
    /// <exception cref="PrinterTimeoutException">Thrown when no acknowledgement arrives in time.</exception>
    public async Task<string> SendPrintJobAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        if (job == null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        if (!_connected)
        {
            throw new InvalidOperationException("Client is not connected. Call ConnectAsync first.");
        }

        string payload = job.ToPayload();

        // [2026-09-17] Validate before touching the transport (F10). A rejected payload
        // is a caller error, not a link failure, so it must not cost an ACK round-trip
        // or leave the FIFO mirror out of step with the printer.
        CodenetFrame.ValidatePrintJobPayload(payload);

        byte[] frame = CodenetFrame.BuildPrintJobFrame(payload);

        bool acknowledged = await SendWithAckAsync(frame, "print job", cancellationToken).ConfigureAwait(false);

        if (!acknowledged)
        {
            throw new PrinterNackException(
                "Printer rejected the print job with NAK (0x15). The on-board FIFO queue is likely full "
                + "(capacity " + CodenetFrame.FIFO_CAPACITY.ToString() + "). Payload: " + payload);
        }

        string jobId = NextJobId();
        _fifoMirror.Enqueue(jobId, payload);

        if (_stateMachine.State != PrinterState.Alarm)
        {
            _stateMachine.TransitionTo(PrinterState.Printing);
        }

        return jobId;
    }

    /// <summary>
    /// Ask the printer how many jobs remain queued, falling back to the local FIFO
    /// mirror when the printer does not answer the query.
    /// </summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The number of jobs currently pending in the FIFO.</returns>
    public async Task<int> GetFifoQueueCountAsync(CancellationToken cancellationToken = default)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Client is not connected. Call ConnectAsync first.");
        }

        try
        {
            await SendWithAckAsync(CodenetFrame.BuildFifoQueryFrame(), "FIFO query", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PrinterTimeoutException)
        {
            LogTraffic("INFO", "FIFO query got no reply; reporting mirrored count.");
        }
        catch (PrinterNackException)
        {
            LogTraffic("INFO", "FIFO query rejected; reporting mirrored count.");
        }

        return _fifoMirror.Count;
    }

    /// <summary>Return a status snapshot combining connection state, FIFO depth and state label.</summary>
    public PrinterStatus GetStatus()
    {
        return new PrinterStatus(_connected, _fifoMirror.Count, _stateMachine.State.ToString());
    }

    /// <summary>Close the serial link and stop the receive loop. Safe to call repeatedly.</summary>
    public Task DisconnectAsync()
    {
        CloseInternal();
        return Task.CompletedTask;
    }

    /// <summary>Release all resources.</summary>
    public void Dispose()
    {
        _disposed = true;
        CloseInternal();
        _commandLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ============================================================
    // Command / acknowledgement core
    // ============================================================

    /// <summary>
    /// Write a frame and wait for the printer to answer it. Serialised by
    /// <c>_commandLock</c> because the printer sends a bare ACK with no command
    /// correlation field, so only one command may be outstanding at a time.
    /// </summary>
    /// <returns><c>true</c> for ACK (<c>0x06</c>), <c>false</c> for NAK (<c>0x15</c>).</returns>
    /// <exception cref="PrinterTimeoutException">Thrown when no answer arrives in time.</exception>
    private async Task<bool> SendWithAckAsync(byte[] frame, string description, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (_ackLock)
            {
                _ackReceived = false;
                _ackValue = false;
            }

            try
            {
                WriteFrame(frame);
            }
            catch (System.Exception ex) when (ex is System.IO.IOException || ex is System.ObjectDisposedException)
            {
                LogTraffic("WARN", "Write failed for '" + description + "': " + ex.Message);
                HandleConnectionLoss();
                throw new PrinterTimeoutException(
                    "Write failed for '" + description + "'; the connection was lost.", ex);
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(_responseTimeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                lock (_ackLock)
                {
                    if (_ackReceived)
                    {
                        return _ackValue;
                    }
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

            LogTraffic("WARN", "Command '" + description + "' timed out; treating the link as lost.");
            HandleConnectionLoss();

            throw new PrinterTimeoutException(
                "No acknowledgement for '" + description + "' within "
                + _responseTimeoutMs.ToString() + " ms.");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>Write raw frame bytes to the serial port under the I/O lock.</summary>
    private void WriteFrame(byte[] frame)
    {
        lock (_ioLock)
        {
            SerialPort? port = _port;
            if (port == null || !port.IsOpen)
            {
                throw new InvalidOperationException("Connection is not open; cannot write.");
            }

            port.Write(frame, 0, frame.Length);
        }

        LogTraffic("TX", CodenetFrame.ToHexString(frame, frame.Length));
    }

    // ============================================================
    // Receive loop
    // ============================================================

    /// <summary>
    /// The single reader. Reads bytes from the serial port (polling read with a short
    /// timeout so the loop stays responsive), then feeds them through the shared frame
    /// splitter.
    /// </summary>
    private void ReceiveLoop()
    {
        byte[] chunk = new byte[512];

        while (_running)
        {
            try
            {
                SerialPort? port;
                lock (_ioLock)
                {
                    port = _port;
                }

                if (port == null || !port.IsOpen)
                {
                    Thread.Sleep(10);
                    continue;
                }

                int read;
                try
                {
                    // [2026-09-17] Hold the port lock during the blocking read so the writer
                    // thread and reader thread never touch the SerialPort concurrently. The
                    // short read timeout lets the loop re-check _running and notice a close.
                    lock (_ioLock)
                    {
                        port.ReadTimeout = SerialPollMs;
                        read = port.Read(chunk, 0, chunk.Length);
                    }
                }
                catch (TimeoutException)
                {
                    // No data within the poll window; loop and re-check _running.
                    continue;
                }
                catch (InvalidOperationException)
                {
                    // Port closed/disposed under us (e.g. during DisconnectAsync).
                    break;
                }
                catch (IOException)
                {
                    LogTraffic("WARN", "Serial read failure.");
                    HandleConnectionLoss();
                    break;
                }

                if (read <= 0)
                {
                    continue;
                }

                byte[] received = new byte[read];
                Array.Copy(chunk, 0, received, 0, read);
                LogTraffic("RX", CodenetFrame.ToHexString(received, read));
                ProcessBytes(received);
            }
            catch (Exception ex)
            {
                LogTraffic("ERROR", "Unexpected receive error: " + ex.Message);
                HandleConnectionLoss();
                break;
            }
        }
    }

    /// <summary>
    /// Feed received bytes through the protocol splitter (shared logic with the TCP
    /// client): single-byte control events (ACK/NAK/0x32) are consumed immediately,
    /// and anything starting with <c>0x1B</c> is held until its <c>0x04</c> terminator.
    /// </summary>
    private void ProcessBytes(byte[] data)
    {
        lock (_rxLock)
        {
            _rxBuffer.AddRange(data);

            if (_rxBuffer.Count > MaxRxBufferBytes)
            {
                LogTraffic("WARN", "Receive buffer exceeded " + MaxRxBufferBytes.ToString()
                    + " bytes; discarding " + _rxBuffer.Count.ToString()
                    + " buffered bytes to resync.");
                _rxBuffer.Clear();
                return;
            }

            while (_rxBuffer.Count > 0)
            {
                byte first = _rxBuffer[0];

                if (first == CodenetFrame.ACK)
                {
                    _rxBuffer.RemoveAt(0);
                    SetAck(true);
                    continue;
                }

                if (first == CodenetFrame.NAK)
                {
                    _rxBuffer.RemoveAt(0);
                    SetAck(false);
                    _stateMachine.ReportAlarm();
                    continue;
                }

                if (first == CodenetFrame.PRINT_DONE)
                {
                    _rxBuffer.RemoveAt(0);
                    RaiseJobCompleted();
                    continue;
                }

                if (first == CodenetFrame.ESC)
                {
                    if (!CodenetFrame.TryParseFrame(_rxBuffer, out byte[] frame))
                    {
                        return;
                    }

                    _rxBuffer.RemoveRange(0, frame.Length);
                    LogTraffic("FRAME", CodenetFrame.ToHexString(frame, frame.Length));
                    continue;
                }

                _rxBuffer.RemoveAt(0);
                LogTraffic("INFO", "Discarded unrecognised byte 0x" + CodenetFrame.ToHexByte(first));
            }
        }
    }

    /// <summary>Record an acknowledgement result for the waiting caller.</summary>
    private void SetAck(bool value)
    {
        lock (_ackLock)
        {
            _ackReceived = true;
            _ackValue = value;
        }

        if (value && _stateMachine.State == PrinterState.Alarm)
        {
            _stateMachine.TransitionTo(PrinterState.Idle);
        }
    }

    /// <summary>
    /// Correlate an arriving <c>0x32</c> with the oldest mirrored job and raise the
    /// completion event.
    /// </summary>
    private void RaiseJobCompleted()
    {
        JobQueueEntry? entry = _fifoMirror.DequeueOldest();

        PrinterEventArgs args = entry != null
            ? new PrinterEventArgs(entry.JobId, entry.CodeValue, DateTime.Now)
            : new PrinterEventArgs(string.Empty, string.Empty, DateTime.Now);

        if (entry == null)
        {
            LogTraffic("WARN", "Uncorrelated 0x32 completion received; raising OnJobCompleted with empty JobId/CodeValue.");
        }

        if (_fifoMirror.Count == 0 && _stateMachine.State == PrinterState.Printing)
        {
            _stateMachine.TransitionTo(PrinterState.Idle);
        }

        OnJobCompleted?.Invoke(this, args);
    }

    // ============================================================
    // Connection lifecycle
    // ============================================================

    /// <summary>
    /// React to a lost connection: tear down the transport and, when auto-reconnect is
    /// enabled, start a retry loop in the background.
    /// </summary>
    private void HandleConnectionLoss()
    {
        bool wasConnected = _connected;

        CloseInternal();

        if (!wasConnected)
        {
            return;
        }

        if (_autoReconnect && !_disposed)
        {
            _running = true;

            OnReconnecting?.Invoke(this, EventArgs.Empty);

            if (System.Threading.Interlocked.CompareExchange(ref _reconnecting, 1, 0) == 0)
            {
                _ = Task.Run(ReconnectLoop);
            }
        }
    }

    /// <summary>
    /// Retry <see cref="ConnectAsync"/> until it succeeds or the client is disposed.
    /// </summary>
    private async Task ReconnectLoop()
    {
        try
        {
            while (_autoReconnect && _running && !_connected && !_disposed)
            {
                try
                {
                    await Task.Delay(_reconnectDelayMs).ConfigureAwait(false);

                    if (!_running || _connected)
                    {
                        return;
                    }

                    LogTraffic("INFO", "Attempting reconnect to " + _portName + " ...");
                    await ConnectAsync().ConfigureAwait(false);

                    if (_disposed || !_running)
                    {
                        return;
                    }

                    LogTraffic("INFO", "Reconnect succeeded.");
                    return;
                }
                catch (Exception ex)
                {
                    LogTraffic("WARN", "Reconnect attempt failed: " + ex.Message);
                }
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    /// <summary>
    /// Tear down the serial transport. Never throws.
    /// </summary>
    private void CloseInternal()
    {
        bool wasConnected = _connected;

        _running = false;
        _connected = false;

        // [2026-09-17] Close the port WITHOUT holding _ioLock: the blocked reader's
        // port.Read() then throws and the loop exits. Closing under the lock would
        // deadlock against a reader that holds the lock for the duration of its read.
        SerialPort? port = _port;
        _port = null;
        try
        {
            if (port != null)
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
                port.Dispose();
            }
        }
        catch (Exception)
        {
            // A port unplugged on site may throw on close; the release flow must finish.
        }

        int pendingJobs = _fifoMirror.Count;
        if (pendingJobs > 0)
        {
            LogTraffic("WARN", pendingJobs.ToString()
                + " in-flight job id(s) discarded due to connection loss/reset.");
        }

        _fifoMirror.Clear();

        // Ask the receive loop to finish, but never block forever. Skip a self-join:
        // HandleConnectionLoss can be reached from the receive thread itself.
        Thread? receive = _receiveThread;
        if (receive != null && receive.IsAlive && receive != Thread.CurrentThread)
        {
            try
            {
                receive.Join(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // A failed join must not prevent teardown from completing.
            }
        }

        _receiveThread = null;

        if (wasConnected)
        {
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }

        _stateMachine.TransitionTo(PrinterState.Disconnected);
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>Generate a monotonically increasing, process-unique job identifier.</summary>
    private string NextJobId()
    {
        long sequence = Interlocked.Increment(ref _jobSequence);
        return "JOB-" + sequence.ToString("D6");
    }

    /// <summary>Emit a raw-traffic log line when a sink is attached.</summary>
    private void LogTraffic(string direction, string payload)
    {
        Action<string>? sink = TrafficLogger;
        if (sink == null)
        {
            return;
        }

        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] ["
                      + direction.PadRight(7) + "] " + payload;

        try
        {
            sink(line);
        }
        catch (Exception)
        {
            // A throwing sink must never break the receive or send path.
        }
    }
}

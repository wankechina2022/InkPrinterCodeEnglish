using System.Net.Sockets;
using System.Text;
using DominoA200Sdk.Core;
using DominoA200Sdk.Exceptions;
using DominoA200Sdk.Models;

namespace DominoA200Sdk;

/// <summary>
/// The public entry point for talking to a Domino A200+ printer over the Codenet
/// protocol.
///
/// <para>
/// <b>What this class hides.</b> Everything below the API surface: TCP socket
/// management, frame assembly and parsing, packet-sticking (coalesced reads),
/// ACK/NAK correlation, timeout handling, FIFO mirroring, unsolicited
/// <c>0x32</c> print-complete events, and automatic reconnect.
/// </para>
///
/// <para>
/// <b>Typical use.</b>
/// <code>
/// var printer = new DominoA200Client("127.0.0.1", 7000);
/// await printer.ConnectAsync();
///
/// printer.OnJobCompleted += (sender, args) =&gt;
///     Console.WriteLine($"Job finished, JobId:{args.JobId}");
///
/// string jobId = await printer.SendPrintJobAsync(new PrintJob("2026-09-15", "ABC123456"));
/// int pending = await printer.GetFifoQueueCountAsync();
/// await printer.DisconnectAsync();
/// </code>
/// </para>
///
/// <para>
/// <b>Threading.</b> One background receive loop owns all reads; public methods are
/// safe to call concurrently from multiple threads. <see cref="OnJobCompleted"/> is
/// raised on the receive loop thread, so a handler must marshal to the UI thread if
/// it touches UI objects.
/// </para>
/// </summary>
public sealed class DominoA200Client : IDisposable
{
    // ============================================================
    // Configuration
    // ============================================================

    private readonly string _host;
    private readonly int _port;
    private readonly int _responseTimeoutMs;
    private readonly int _connectTimeoutMs;
    private readonly int _reconnectDelayMs;
    private readonly bool _autoReconnect;

    // ============================================================
    // Transport and protocol state
    // ============================================================

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly object _ioLock = new object();
    // [2026-09-16] Dedicated lock for the connect sequence so a user call racing the
    // background ReconnectLoop cannot open two sockets (F2).
    private readonly object _connectLock = new object();

    private Thread? _receiveThread;
    private volatile bool _running;
    private volatile bool _connected;
    // [2026-09-15] Guards the reconnect loop: once Dispose() runs, HandleConnectionLoss
    // must not re-arm the loop or let a pending reconnect resurrect the client.
    private volatile bool _disposed;
    // [2026-09-16] Connect-in-progress flag (F2): concurrent callers see this and
    // return instead of double-connecting.
    private volatile bool _connecting;
    // [2026-09-16] Single-instance guard for the reconnect loop (F2). 0 = idle,
    // 1 = a ReconnectLoop is running; set with CompareExchange, cleared on exit.
    private int _reconnecting;
    // [2026-09-16] Set by CloseInternal, cleared at the start of the next ConnectAsync.
    // Distinguishes "an intentional close landed while a connect was in flight" from
    // "a fresh client that has simply never connected" — _running is false in BOTH
    // cases, so the earlier F3 check "_disposed || !_running" silently aborted every
    // first connect (seen as "Client is not connected" on the first SendPrintJobAsync).
    private volatile bool _closeRequested;

    private readonly List<byte> _rxBuffer = new List<byte>(256);
    // [2026-09-16] Hard cap on the reassembly buffer (F4): an orphan ESC never
    // followed by 0x04, or any malformed stream, can no longer grow it without bound.
    private const int MaxRxBufferBytes = 64 * 1024;
    private readonly object _rxLock = new object();

    private readonly PrinterStateMachine _stateMachine = new PrinterStateMachine();
    private readonly JobQueue _fifoMirror = new JobQueue();

    // Pending acknowledgement: the receive loop fills this in when a 0x06 / 0x15
    // arrives, and the waiting caller consumes it. Only one command may be in flight
    // at a time, which the 200 ms submit rhythm and the _commandLock enforce.
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

    /// <summary>
    /// Raised when the printer pushes a <c>0x32</c> print-complete event. The payload
    /// carries the correlated job id when the SDK could match it to its FIFO mirror.
    /// Raised on the background receive thread.
    /// </summary>
    public event EventHandler<PrinterEventArgs>? OnJobCompleted;

    /// <summary>Raised whenever the transport connection is established.</summary>
    public event EventHandler? OnConnected;

    /// <summary>Raised whenever the transport connection is lost or closed.</summary>
    public event EventHandler? OnDisconnected;

    /// <summary>Raised for every state-machine transition.</summary>
    public event EventHandler<PrinterStateChangedEventArgs>? OnStateChanged;

    /// <summary>
    /// Raised when a receive failure is detected and the client is about to attempt a
    /// reconnect. Only fires when <c>autoReconnect</c> is enabled.
    /// </summary>
    public event EventHandler? OnReconnecting;

    // ============================================================
    // Construction
    // ============================================================

    /// <summary>
    /// Create a client bound to a printer endpoint. No connection is made until
    /// <see cref="ConnectAsync"/> is called.
    /// </summary>
    /// <param name="host">Printer host or IP address.</param>
    /// <param name="port">Codenet TCP port. The A200+ listens on 7000 in this demo.</param>
    /// <param name="responseTimeoutMs">How long to wait for an ACK/NAK before treating
    /// the command as timed out. Defaults to 3000 ms.</param>
    /// <param name="connectTimeoutMs">Connect timeout. Defaults to 5000 ms.</param>
    /// <param name="autoReconnect">When true, a lost connection triggers a background
    /// reconnect loop. Defaults to false so the caller stays in control.</param>
    /// <param name="reconnectDelayMs">Delay between reconnect attempts. Defaults to 2000 ms.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="host"/> is null or whitespace.</exception>
    public DominoA200Client(
        string host,
        int port = 7000,
        int responseTimeoutMs = 3000,
        int connectTimeoutMs = 5000,
        bool autoReconnect = false,
        int reconnectDelayMs = 2000)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Printer host must not be null or empty.", nameof(host));
        }

        _host = host.Trim();
        _port = port;
        _responseTimeoutMs = responseTimeoutMs > 0 ? responseTimeoutMs : 3000;
        _connectTimeoutMs = connectTimeoutMs > 0 ? connectTimeoutMs : 5000;
        _autoReconnect = autoReconnect;
        _reconnectDelayMs = reconnectDelayMs > 0 ? reconnectDelayMs : 2000;

        _stateMachine.StateChanged += (sender, args) => OnStateChanged?.Invoke(this, args);
    }

    // ============================================================
    // Public surface
    // ============================================================

    /// <summary>Whether the transport connection is currently up.</summary>
    public bool IsConnected
    {
        get { return _connected; }
    }

    /// <summary>The current high-level printer state.</summary>
    public PrinterState State
    {
        get { return _stateMachine.State; }
    }

    /// <summary>Host the client is configured to reach.</summary>
    public string Host
    {
        get { return _host; }
    }

    /// <summary>Port the client is configured to reach.</summary>
    public int Port
    {
        get { return _port; }
    }

    /// <summary>
    /// Open the connection, start the receive loop and run the protocol handshake
    /// (print-signal setup plus queue clear).
    /// </summary>
    /// <param name="cancellationToken">Cancels the connect attempt.</param>
    /// <exception cref="SocketException">Thrown when the TCP connect fails.</exception>
    /// <exception cref="TimeoutException">Thrown when the connect exceeds the timeout.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // [2026-09-16] Concurrency guard (F2): make the "already connected/connecting"
        // check atomic and gate the rest behind the _connecting flag so a user call
        // racing the background ReconnectLoop cannot open two sockets or spawn two
        // receive threads for one logical connection. The lock is released before any
        // await because Monitor is not async-aware (holding it across awaits would
        // risk a SynchronizationLockException).
        bool lockTaken = false;
        try
        {
            System.Threading.Monitor.Enter(_connectLock, ref lockTaken);
            if (_connected || _connecting)
            {
                return;
            }
            _connecting = true;

            // [2026-09-16] A fresh connect attempt supersedes any close request that
            // landed before it (e.g. reconnecting after a clean DisconnectAsync).
            _closeRequested = false;
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
            // [2026-09-17] Connect straight to the CodeNet data port (default 7000, the
            // real printer's port). We intentionally do NOT send the legacy 700-port
            // pre-reset command: the field-tested InkPrinterCode has that path disabled
            // (resetPort = 0), so the SDK must not write anything to port 700 either.
            TcpClient client = new TcpClient();

            Task connectTask = client.ConnectAsync(_host, _port);

            Task completed = await Task.WhenAny(connectTask, Task.Delay(_connectTimeoutMs, cancellationToken))
                .ConfigureAwait(false);

            if (completed != connectTask)
            {
                client.Close();
                // [2026-09-16] Observe the abandoned connect task (F8-a) so a faulted
                // connect (e.g. connection refused) never becomes an unobserved-task
                // exception after we time out here.
                _ = connectTask.ContinueWith(t => { try { t.GetAwaiter().GetResult(); } catch { } },
                    System.Threading.Tasks.TaskScheduler.Default);
                throw new TimeoutException(
                    "Timed out connecting to printer " + _host + ":" + _port.ToString()
                    + " after " + _connectTimeoutMs.ToString() + " ms.");
            }

            // Surface any connect fault (connection refused, unreachable host, ...).
            await connectTask.ConfigureAwait(false);

            client.ReceiveTimeout = _responseTimeoutMs;
            client.SendTimeout = _responseTimeoutMs;

            // [2026-09-16] Re-check after the awaits (F3): a Dispose() or intentional
            // close may have landed while we were establishing the socket. Drop the
            // socket so a disposed/closed client is never revived with a live stream.
            // [2026-09-16 fix] _running is deliberately NOT checked here — it is false
            // on a fresh client too, and the old "_disposed || !_running" check
            // silently aborted every first connect. The _closeRequested flag covers
            // the intentional-close case without breaking the first-connect path.
            if (_disposed || _closeRequested)
            {
                client.Close();
                return;
            }

            lock (_ioLock)
            {
                _tcpClient = client;
                _stream = client.GetStream();
            }

        _running = true;
        _connected = true;
        _stateMachine.TransitionTo(PrinterState.Idle);

        _receiveThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "DominoA200.Receive"
        };
        _receiveThread.Start();

        OnConnected?.Invoke(this, EventArgs.Empty);
        LogTraffic("CONNECT", _host + ":" + _port.ToString());

        // Protocol handshake: tell the printer to report print completion, then clear
        // any stale queued data so our FIFO mirror starts from a known state.
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
            // [2026-09-16] Always release the connect-in-progress flag so a later call
            // (including a queued concurrent caller or the next reconnect attempt) can
            // proceed.
            _connecting = false;
        }
    }

    /// <summary>
    /// Submit a print job.
    ///
    /// <para>
    /// The method writes the job frame and waits for the acknowledgement. A
    /// <c>0x06</c> means the printer accepted the job, which is then added to the FIFO
    /// mirror and its generated job id returned. A <c>0x15</c> means rejection — most
    /// often because the on-board queue is full — and raises
    /// <see cref="PrinterNackException"/>.
    /// </para>
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

        // A queued job means the printer now has work in flight. The state returns to
        // Idle when the mirror drains (see RaiseJobCompleted).
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

        // The A200+ does not return a numeric depth over this command path in the
        // observed capture, so the query is best-effort: it keeps the printer's own
        // accounting in step, while the authoritative answer comes from the mirror
        // the SDK maintains from acknowledgements and completion events.
        try
        {
            await SendWithAckAsync(CodenetFrame.BuildFifoQueryFrame(), "FIFO query", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PrinterTimeoutException)
        {
            // A missing reply to a read-only query must not fail the caller.
            LogTraffic("INFO", "FIFO query got no reply; reporting mirrored count.");
        }
        catch (PrinterNackException)
        {
            LogTraffic("INFO", "FIFO query rejected; reporting mirrored count.");
        }

        return _fifoMirror.Count;
    }

    /// <summary>
    /// Return a status snapshot combining connection state, FIFO depth and state label.
    /// </summary>
    public PrinterStatus GetStatus()
    {
        return new PrinterStatus(_connected, _fifoMirror.Count, _stateMachine.State.ToString());
    }

    /// <summary>
    /// Close the connection and stop the receive loop. Safe to call repeatedly.
    /// <para>
    /// <b>Note.</b> This method is effectively synchronous (F8-b): it blocks up to
    /// ~1 second while <see cref="CloseInternal"/> joins the background receive
    /// thread, then returns an already-completed task.
    /// </para>
    /// </summary>
    public Task DisconnectAsync()
    {
        CloseInternal();
        return Task.CompletedTask;
    }

    /// <summary>Release all resources.</summary>
    public void Dispose()
    {
        // [2026-09-15] Set before teardown so an in-flight reconnect loop stops and
        // HandleConnectionLoss cannot re-arm it afterwards.
        _disposed = true;

        CloseInternal();
        _commandLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ============================================================
    // Command / acknowledgement core
    // ============================================================

    /// <summary>
    /// Write a frame and wait for the printer to answer it.
    ///
    /// <para>
    /// <b>Why serialized.</b> The A200+ sends a bare ACK byte with no command
    /// correlation field, so two commands in flight simultaneously would make the
    /// acknowledgement ambiguous. The semaphore guarantees exactly one command is
    /// outstanding at a time.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> for ACK (<c>0x06</c>), <c>false</c> for NAK (<c>0x15</c>).</returns>
    /// <exception cref="PrinterTimeoutException">Thrown when no answer arrives in time.</exception>
    private async Task<bool> SendWithAckAsync(byte[] frame, string description, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Arm the mailbox before writing: the reply can arrive on the receive
            // thread before Write returns, and a late arm would drop it.
            lock (_ackLock)
            {
                _ackReceived = false;
                _ackValue = false;
            }

            // [2026-09-16] A failed write (IOException / ObjectDisposedException) means
            // the link is already gone. Mark the connection as lost so the optional
            // auto-reconnect path engages, then surface the existing contract exception
            // naming the failed command (F7).
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

            // A timeout means the bytes went out but nothing ever came back. On TCP a
            // half-open link still accepts writes, so this is the only signal that the
            // peer has stopped answering. Mark the link as lost before throwing, so the
            // optional auto-reconnect path engages instead of leaving the client
            // "connected" but permanently silent.
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

    /// <summary>Write raw frame bytes to the socket under the I/O lock.</summary>
    private void WriteFrame(byte[] frame)
    {
        lock (_ioLock)
        {
            if (_stream == null)
            {
                throw new InvalidOperationException("Connection is not open; cannot write.");
            }

            _stream.Write(frame, 0, frame.Length);
            _stream.Flush();
        }

        LogTraffic("TX", CodenetFrame.ToHexString(frame, frame.Length));
    }

    // ============================================================
    // Receive loop
    // ============================================================

    /// <summary>
    /// The single reader. Interprets the byte stream frame by frame, dispatching
    /// acknowledgements, print-complete events and unsolicited frames.
    /// </summary>
    private void ReceiveLoop()
    {
        byte[] chunk = new byte[512];

        while (_running)
        {
            try
            {
                NetworkStream? stream;
                lock (_ioLock)
                {
                    stream = _stream;
                }

                if (stream == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (!stream.DataAvailable)
                {
                    // [2026-09-16] Detect a graceful peer close (FIN). NetworkStream
                    // leaves DataAvailable false after a FIN, so a socket that is
                    // readable but has zero bytes means EOF. Without this probe the loop
                    // would sleep forever and never notice the lost connection (F1).
                    if (_tcpClient != null && _tcpClient.Client != null
                        && _tcpClient.Client.Poll(0, System.Net.Sockets.SelectMode.SelectRead)
                        && _tcpClient.Client.Available == 0)
                    {
                        LogTraffic("WARN", "Peer closed the connection (graceful FIN).");
                        HandleConnectionLoss();
                        break;
                    }

                    Thread.Sleep(5);
                    continue;
                }

                int read = stream.Read(chunk, 0, chunk.Length);

                // [2026-09-16] A zero/negative read means the peer closed the
                // connection. Treat it the same as a read exception (F1).
                if (read <= 0)
                {
                    LogTraffic("WARN", "Read returned " + read.ToString() + " bytes; peer closed the connection.");
                    HandleConnectionLoss();
                    break;
                }

                byte[] received = new byte[read];
                Array.Copy(chunk, 0, received, 0, read);
                LogTraffic("RX", CodenetFrame.ToHexString(received, received.Length));
                ProcessBytes(received);
            }
            catch (ObjectDisposedException)
            {
                // The stream was closed under us during shutdown; leave the loop.
                break;
            }
            catch (IOException ex)
            {
                LogTraffic("WARN", "Read failure: " + ex.Message);
                HandleConnectionLoss();
                break;
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
    /// Feed received bytes through the protocol splitter.
    ///
    /// <para>
    /// <b>Packet sticking.</b> TCP delivers a stream, not messages. A single read may
    /// contain a bare event byte followed by a frame start, so the buffer is drained
    /// byte by byte: single-byte events are consumed immediately, and anything
    /// starting with <c>0x1B</c> is held until its <c>0x04</c> terminator arrives.
    /// </para>
    /// </summary>
    private void ProcessBytes(byte[] data)
    {
        lock (_rxLock)
        {
            _rxBuffer.AddRange(data);

            // [2026-09-16] Cap the reassembly buffer (F4). A malformed stream that never
            // sends the 0x04 terminator (e.g. an orphan 0x1B) would otherwise grow the
            // buffer without bound. If we exceed the cap, log and discard to resync
            // rather than throwing.
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

                // ---- Single-byte control events ----
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

                    // A rejection usually means the printer-side queue is saturated.
                    _stateMachine.ReportAlarm();
                    continue;
                }

                if (first == CodenetFrame.PRINT_DONE)
                {
                    _rxBuffer.RemoveAt(0);
                    RaiseJobCompleted();
                    continue;
                }

                // ---- Terminated frame ----
                if (first == CodenetFrame.ESC)
                {
                    if (!CodenetFrame.TryParseFrame(_rxBuffer, out byte[] frame))
                    {
                        // Incomplete frame: wait for the next read to complete it.
                        return;
                    }

                    _rxBuffer.RemoveRange(0, frame.Length);
                    LogTraffic("FRAME", CodenetFrame.ToHexString(frame, frame.Length));
                    continue;
                }

                // ---- Unrecognised byte: drop it and note it in the log ----
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

        if (value)
        {
            // Any successful exchange proves the link is healthy again.
            if (_stateMachine.State == PrinterState.Alarm)
            {
                _stateMachine.TransitionTo(PrinterState.Idle);
            }
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

        // [2026-09-16] An unsolicited 0x32 with no matching mirrored job (uncorrelated
        // completion) is not an error, but it is worth a warning: the caller's JobId and
        // CodeValue will be empty on this event. We still raise it so subscribers always
        // learn that a print finished (F6).
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
    /// React to a lost connection: tear down the transport and, when auto-reconnect
    /// is enabled, start a retry loop in the background.
    /// </summary>
    private void HandleConnectionLoss()
    {
        bool wasConnected = _connected;

        CloseInternal();

        if (!wasConnected)
        {
            return;
        }

        // [2026-09-15] Fix: CloseInternal() clears _running, which is exactly the flag
        // the reconnect loop checks — so with auto-reconnect enabled the recovery loop
        // could never start (caught by AutoReconnect_EngagesWhenCommandTimesOut: no
        // OnReconnecting ever fired after a command timeout). Re-arm the flag here.
        // The old receive thread has already been joined by CloseInternal(), and
        // ConnectAsync() will start a fresh one once the printer answers again.
        // OnDisconnected and the Disconnected transition are intentionally NOT raised
        // here: CloseInternal() already delivered both for this loss path (this also
        // removes a double-fire of OnDisconnected).
        if (_autoReconnect && !_disposed)
        {
            _running = true;

            OnReconnecting?.Invoke(this, EventArgs.Empty);

            // [2026-09-16] Guarantee only one reconnect loop at a time (F2): if a loop
            // is already running, don't spawn a second one.
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
        // [2026-09-15] Added !_disposed to the loop condition: a reconnect started just
        // before Dispose() must not keep retrying (or silently reconnect) afterwards.
        while (_autoReconnect && _running && !_connected && !_disposed)
        {
            try
            {
                await Task.Delay(_reconnectDelayMs).ConfigureAwait(false);

                if (!_running || _connected)
                {
                    return;
                }

                LogTraffic("INFO", "Attempting reconnect to " + _host + ":" + _port.ToString() + " ...");
                await ConnectAsync().ConfigureAwait(false);

                // [2026-09-16] Re-check after the await (F3): the client may have been
                // disposed or intentionally closed while we were reconnecting.
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
            // [2026-09-16] Release the single-instance guard so a future connection loss
            // can start a fresh reconnect loop (F2).
            System.Threading.Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    /// <summary>
    /// Close the socket, stop the receive loop and reset the mirrored state. Safe to
    /// call repeatedly and from any thread.
    /// </summary>
    private void CloseInternal()
    {
        bool wasConnected = _connected;

        // [2026-09-16] Mark that a close was requested. An in-flight ConnectAsync reads
        // this after its awaits and aborts instead of reviving a closed client. Cleared
        // by the next ConnectAsync, so reconnecting after a clean disconnect still works
        // (and the reconnect loop's own ConnectAsync call consumes it as well).
        _closeRequested = true;

        _running = false;
        _connected = false;

        lock (_ioLock)
        {
            // [2026-09-17] Forced RST close, matching the field-tested InkPrinterCode
            // (TcpPrinterConnection.Close). The A200+ firmware does not release the
            // connection slot on a plain FIN, so repeated reconnects pile up and the
            // printer reports "connection count reached". We therefore:
            //   (1) Shutdown(SocketShutdown.Both) — send a polite FIN for notification;
            //   (2) LingerOption(true, 0) — mark the socket so the next close issues an
            //       RST instead of a FIN (documented Windows behavior; no TIME_WAIT);
            //   (3) close the stream then the client — the RST goes out here.
            // The LingerState MUST be set before disposing the stream, because
            // NetworkStream.Dispose releases the underlying socket.
            TcpClient? tcpClient = _tcpClient;
            NetworkStream? stream = _stream;
            _tcpClient = null;
            _stream = null;

            Socket? socket = tcpClient?.Client;
            if (socket != null)
            {
                // (1) Polite FIN; already-dead sockets throw here, ignore it.
                try
                {
                    if (socket.Connected)
                    {
                        socket.Shutdown(SocketShutdown.Both);
                    }
                }
                catch (Exception)
                {
                    // Socket already dead/disposed; no notification needed.
                }

                // (2) Mark for RST on the subsequent close (degrades to FIN if this fails).
                try
                {
                    socket.LingerState = new LingerOption(true, 0);
                }
                catch (Exception)
                {
                    // If it fails, a normal FIN close is no worse than before.
                }
            }

            // (3) Close: stream first, then client; RST is emitted here.
            try
            {
                stream?.Dispose();
            }
            catch (Exception)
            {
                // Disposing an already-broken stream is expected during teardown.
            }

            try
            {
                tcpClient?.Close();
            }
            catch (Exception)
            {
                // Same rationale: a dead socket may throw while closing.
            }
        }

        // [2026-09-16] Make the silent mirror reset observable (F6): dropping in-flight
        // job ids is exactly the kind of thing that breaks a caller's FIFO accounting
        // without a trace, so warn first when there is something to discard.
        int pendingJobs = _fifoMirror.Count;
        if (pendingJobs > 0)
        {
            LogTraffic("WARN", pendingJobs.ToString()
                + " in-flight job id(s) discarded due to connection loss/reset.");
        }

        _fifoMirror.Clear();

        // Ask the receive loop to finish, but never block forever: it is a background
        // thread and the process must be able to exit.
        //
        // Important: HandleConnectionLoss can be reached *from* the receive thread
        // (a read failure routes through it). Joining the current thread would block
        // forever, so the self-join case is skipped explicitly.
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
            // A misbehaving log sink must never break the receive path.
        }
    }
}

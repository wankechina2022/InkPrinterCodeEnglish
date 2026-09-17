using System.Net;
using System.Net.Sockets;

namespace DominoMockServer;

/// <summary>
/// A TCP server that speaks enough of the Codenet protocol to stand in for a Domino
/// A200+ printer.
///
/// <para>
/// <b>Why it exists.</b> Anyone who clones the repository can start this process and
/// exercise the SDK immediately, with no printer on the bench. That makes the SDK
/// reviewable and testable, and it is what the automated harness drives.
/// </para>
///
/// <para>
/// <b>Emulated behaviour.</b>
/// </para>
/// <list type="bullet">
///   <item><description>Accepts print jobs while FIFO depth &lt; 3 and answers <c>0x06</c>.</description></item>
///   <item><description>Refuses a job when FIFO depth = 3 and answers <c>0x15</c>.</description></item>
///   <item><description>Simulates printing with a delay, then pushes <c>0x32</c> unprompted.</description></item>
///   <item><description>Logs every exchange as raw hexadecimal bytes.</description></item>
/// </list>
/// </summary>
public sealed class MockPrinter : IDisposable
{
    private const byte ESC = 0x1B;
    private const byte EOT = 0x04;
    private const byte ACK = 0x06;
    private const byte NAK = 0x15;

    /// <summary>Default simulated print duration, in milliseconds.</summary>
    public const int DEFAULT_PRINT_DURATION_MS = 1500;

    private readonly int _port;
    private readonly int _printDurationMs;
    private readonly bool _quiet;
    private readonly CodenetHandler _handler = new CodenetHandler();

    private TcpListener? _listener;
    private Thread? _acceptThread;
    private readonly List<ClientSession> _sessions = new List<ClientSession>();
    private readonly object _sessionsLock = new object();
    private readonly MockFifoQueue _fifo = new MockFifoQueue();
    private long _jobSequence;

    private volatile bool _running;

    // [2026-09-17] When true the server keeps every connection OPEN but answers nothing.
    // This is the only way to reproduce a real-world "printer alive but not responding"
    // condition: stopping the server sends a FIN/RST, which the client notices at once as
    // a connection loss, so the command-timeout path is never exercised. Holding the
    // socket open while staying silent drives exactly that path.
    private volatile bool _silent;

    /// <summary>Raised for every logged line, useful for tests that assert on traffic.</summary>
    public event EventHandler<string>? LogLine;

    /// <summary>
    /// When set to <c>true</c> the server keeps accepting and holding connections but
    /// stops answering any command, so callers observe a timeout rather than a
    /// disconnect. Useful for exercising the client's command-timeout handling.
    /// </summary>
    public bool Silent
    {
        get { return _silent; }
        set
        {
            _silent = value;
            Log("MODE", value ? "Silent: connections stay open, no replies sent."
                              : "Normal: commands are answered again.");
        }
    }

    /// <summary>Creates the mock server.</summary>
    /// <param name="port">TCP port to listen on.</param>
    /// <param name="printDurationMs">How long the simulated print takes before the
    /// <c>0x32</c> completion event is pushed. Defaults to 1500 ms.</param>
    /// <param name="quiet">When true, suppresses console output. Defaults to false.</param>
    public MockPrinter(int port = 7000, int printDurationMs = DEFAULT_PRINT_DURATION_MS, bool quiet = false)
    {
        _port = port;
        _printDurationMs = printDurationMs > 0 ? printDurationMs : DEFAULT_PRINT_DURATION_MS;
        _quiet = quiet;

        // [2026-09-16] Route malformed-frame diagnostics from the protocol handler to
        // this server's log (F5).
        _handler.Log = msg => Log("PARSE", msg);
    }

    /// <summary>Port the server is bound to.</summary>
    public int Port
    {
        get { return _port; }
    }

    /// <summary>
    /// Start listening. Returns as soon as the socket is bound; the accept loop runs
    /// on a background thread.
    /// </summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _listener = new TcpListener(IPAddress.Loopback, _port);

        // [2026-09-16] Allow the port to be reused immediately after a restart so a
        // quick re-run of the mock does not hit TIME_WAIT "address already in use"
        // (F9-d). Both options must be set before the socket binds.
        try
        {
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Server.ExclusiveAddressUse = false;
        }
        catch (SocketException)
        {
            // Best-effort: if the option cannot be set, fall back to the default
            // behaviour rather than failing to start.
        }

        _listener.Start();
        _running = true;

        Thread acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "MockPrinter.Accept"
        };

        // [2026-09-17] Remembered so Stop() can join it. An unjoined accept thread would
        // survive a restart and race the new one for the port (see Stop).
        _acceptThread = acceptThread;
        acceptThread.Start();

        Log("LISTEN", "Mock A200+ listening on 127.0.0.1:" + _port.ToString());
    }

    /// <summary>Stop listening and drop all sessions.</summary>
    public void Stop()
    {
        _running = false;

        try
        {
            _listener?.Stop();
        }
        catch (Exception)
        {
            // Listener teardown is best-effort.
        }

        _listener = null;

        // [2026-09-17] Drop the sessions BEFORE waiting for the accept thread. Disposing
        // the session sockets unblocks a Run() that is waiting on the peer, which is what
        // lets the accept thread reach its loop condition and exit.
        //
        // The list is snapshotted under the lock and disposed outside it: Run() calls
        // RemoveSession from its finally block, so disposing while holding the lock would
        // make that thread wait - and iterating a list another thread may be mutating is
        // not safe.
        ClientSession[] sessions;

        lock (_sessionsLock)
        {
            sessions = _sessions.ToArray();
            _sessions.Clear();
        }

        foreach (ClientSession session in sessions)
        {
            session.Dispose();
        }

        // [2026-09-17] Wait for the accept thread to finish. Without this, a restart
        // (Start after Stop, as the reconnect test does) could leave the old thread alive
        // and two accept loops would compete for the same port, so a fresh client could
        // be picked up by the stale loop and never served.
        Thread? acceptThread = _acceptThread;
        _acceptThread = null;

        if (acceptThread != null && acceptThread.IsAlive)
        {
            if (!acceptThread.Join(TimeSpan.FromSeconds(2)))
            {
                Log("WARN", "Accept thread did not exit within 2 s; it will terminate on the next accept.");
            }
        }

        _fifo.Clear();
        Log("STOP", "Mock A200+ stopped.");
    }

    /// <summary>Release resources.</summary>
    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    // ============================================================
    // Accept loop
    // ============================================================

    private void AcceptLoop()
    {
        // [2026-09-16] Serve ONE client at a time (F9-a), matching a single physical
        // printer: one Codenet port, one session. Accept a connection, handle it to
        // completion (the session loop returns once the client disconnects or the peer
        // drops), then accept the next. This removes the cross-session FIFO contamination
        // a thread-per-client design would allow.
        while (_running)
        {
            try
            {
                TcpListener? listener = _listener;
                if (listener == null)
                {
                    return;
                }

                TcpClient client = listener.AcceptTcpClient();

                // [2026-09-17] Stop() may have landed while we were blocked in accept.
                // Close the socket we just took rather than starting a session for a
                // server that is already shutting down.
                if (!_running)
                {
                    try
                    {
                        client.Close();
                    }
                    catch (Exception)
                    {
                        // Best-effort teardown.
                    }

                    return;
                }

                ClientSession session = new ClientSession(client, this);

                lock (_sessionsLock)
                {
                    _sessions.Add(session);
                }

                Log("CONNECT", "Client accepted; serving exclusively until it disconnects.");

                // Block here until the client disconnects, so only one session is ever
                // active at a time. ClientSession.Run swallows its own read errors in a
                // try/catch, so this call returns cleanly once the client is gone.
                session.Run();
            }
            catch (SocketException)
            {
                // The listener was stopped; exit quietly.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log("ERROR", "Accept loop error: " + ex.Message);
                return;
            }
        }
    }

    // ============================================================
    // Protocol handling (invoked by a session)
    // ============================================================

    /// <summary>
    /// Handle one complete inbound frame. Answers ACK or NAK and, for an accepted
    /// print job, schedules the delayed <c>0x32</c> completion push.
    /// </summary>
    internal void HandleFrame(ClientSession session, byte[] frame)
    {
        // [2026-09-17] Silent mode: absorb the frame and answer nothing, so the caller
        // times out instead of being told to stop. Deliberately does NOT close the socket
        // - a closed socket would look like a disconnect, which is a different failure.
        if (_silent)
        {
            Log("SILENT", "Frame absorbed with no reply: " + ToHex(frame));
            return;
        }

        // Delegate byte-level interpretation to the protocol handler, which knows the
        // command grammar. This keeps the socket lifecycle here and the emulated
        // protocol semantics there.
        CodenetHandler.ParsedCommand command = _handler.Parse(frame);

        switch (command.Kind)
        {
            case CodenetHandler.CommandKind.SignalSetup:
                session.Send(new byte[] { ACK });
                Log("ACK", "Print-signal setup accepted (head 1 will push 0x32).");
                return;

            case CodenetHandler.CommandKind.FifoQuery:
                session.Send(new byte[] { ACK });
                Log("FIFO", "Query answered with count=" + _fifo.Count.ToString());
                return;

            case CodenetHandler.CommandKind.ClearQueue:
                session.Send(new byte[] { ACK });
                _fifo.Clear();
                Log("QUEUE", "Cache queue " + command.QueueIndex.ToString() + " cleared.");
                return;

            case CodenetHandler.CommandKind.PrintJob:
                HandlePrintJob(session, command.Payload);
                return;

            default:
                // Unknown / unsupported command: acknowledge so the client does not stall.
                session.Send(new byte[] { ACK });
                Log("RX", "Unrecognised frame, answered ACK: " + ToHex(frame));
                return;
        }
    }

    /// <summary>
    /// Admit or refuse a print job, mirroring the hardware's three-deep FIFO rule.
    /// </summary>
    private void HandlePrintJob(ClientSession session, string codeText)
    {
        if (_fifo.IsFull)
        {
            // Queue saturated: refuse exactly as the hardware does.
            session.Send(new byte[] { NAK });
            Log("NAK", "FIFO full (" + _fifo.Count.ToString() + "/"
                       + MockFifoQueue.CAPACITY.ToString() + "), rejected: " + codeText);
            return;
        }

        long sequence = Interlocked.Increment(ref _jobSequence);
        MockJob job = new MockJob("MOCK-" + sequence.ToString("D6"), codeText);

        if (!_fifo.TryEnqueue(job))
        {
            session.Send(new byte[] { NAK });
            Log("NAK", "Queued between check and insert, rejected: " + codeText);
            return;
        }

        session.Send(new byte[] { ACK });
        Log("ACK", "Job accepted, FIFO " + _fifo.Count.ToString() + "/"
                   + MockFifoQueue.CAPACITY.ToString() + ": " + codeText);

        ScheduleCompletion(session, job);
    }

    /// <summary>
    /// After the simulated print duration, dequeue the job and push the single
    /// <c>0x32</c> completion byte without being asked.
    /// </summary>
    private void ScheduleCompletion(ClientSession session, MockJob job)
    {
        // [2026-09-16] Replace the per-job thread (F9-b) with a timer continuation: no
        // thread is blocked for the print duration, which avoids thread leaks under load
        // and keeps completion timing accurate.
        System.Threading.Tasks.Task delayTask = System.Threading.Tasks.Task.Delay(_printDurationMs);
        _ = delayTask.ContinueWith(_ =>
        {
            try
            {
                if (!_running)
                {
                    return;
                }

                if (session.IsClosed)
                {
                    // [2026-09-16] The client disconnected before its print finished
                    // (F9-c). Keep the existing suppression (do not push 0x32 to a dead
                    // socket), but record that the job actually completed with no one to
                    // tell.
                    Log("PUSH", "Job " + job.JobId + " completed but the client was gone: " + job.Payload);
                    return;
                }

                _fifo.DequeueOldest();

                session.Send(new byte[] { 0x32 });
                Log("PUSH", "Print complete (0x32) for " + job.JobId + ": " + job.Payload);
            }
            catch (Exception ex)
            {
                Log("WARN", "Completion push failed: " + ex.Message);
            }
        }, System.Threading.Tasks.TaskScheduler.Default);
    }

    // ============================================================
    // Logging
    // ============================================================

    internal void Log(string tag, string message)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] ["
                      + tag.PadRight(7) + "] " + message;

        if (!_quiet)
        {
            Console.WriteLine(line);
        }

        try
        {
            LogLine?.Invoke(this, line);
        }
        catch (Exception)
        {
            // A log subscriber must not be able to break the server.
        }
    }

    /// <summary>Render bytes as space-separated uppercase hexadecimal.</summary>
    internal static string ToHex(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return string.Empty;
        }

        System.Text.StringBuilder builder = new System.Text.StringBuilder(data.Length * 3);

        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(data[i].ToString("X2"));
        }

        return builder.ToString();
    }

    internal void RemoveSession(ClientSession session)
    {
        lock (_sessionsLock)
        {
            _sessions.Remove(session);
        }
    }

    // ============================================================
    // Nested session
    // ============================================================

    /// <summary>
    /// One accepted client connection: reads bytes, splits frames, and writes replies.
    /// </summary>
    internal sealed class ClientSession : IDisposable
    {
        private readonly TcpClient _client;
        private readonly MockPrinter _owner;
        private readonly NetworkStream _stream;
        private readonly Socket _socket;
        private readonly object _writeLock = new object();
        private volatile bool _closed;

        /// <summary>Whether this session's connection has been closed.</summary>
        internal bool IsClosed => _closed;

        public ClientSession(TcpClient client, MockPrinter owner)
        {
            _client = client;
            _owner = owner;
            _stream = client.GetStream();

            // [2026-09-17] Kept so the read loop can wait on the socket itself and so a
            // dropped peer is noticed promptly (see Run).
            _socket = client.Client;
        }

        /// <summary>Read loop for this connection.</summary>
        public void Run()
        {
            byte[] chunk = new byte[512];
            List<byte> buffer = new List<byte>(256);

            try
            {
                while (!_closed)
                {
                    // [2026-09-17] Detect a dropped peer instead of spinning forever.
                    //
                    // The old loop tested DataAvailable and, when it was false, slept 5 ms
                    // and looped. That never noticed a closed or reset connection:
                    // DataAvailable simply keeps returning false, _closed stays false (only
                    // Dispose sets it), and the loop runs to the end of the process. Because
                    // AcceptLoop calls Run() synchronously, the accept thread was then
                    // parked forever and every later client was queued behind it, never
                    // accepted - which surfaced as "No acknowledgement for 'print signal
                    // setup'" in whichever test connected next.
                    //
                    // Polling the socket lets us see FIN/RST: Available goes to 0 and Poll
                    // reports the error/read-closed condition.
                    if (_socket.Poll(20000, SelectMode.SelectRead) && _socket.Available == 0)
                    {
                        break;   // readable with no data == the peer closed the link
                    }

                    if (!_stream.DataAvailable)
                    {
                        continue;
                    }

                    int read = _stream.Read(chunk, 0, chunk.Length);

                    if (read <= 0)
                    {
                        break;
                    }

                    byte[] received = new byte[read];
                    Array.Copy(chunk, 0, received, 0, read);
                    _owner.Log("RX", ToHex(received));
                    buffer.AddRange(received);

                    // Drain complete frames; leave a partial frame in the buffer.
                    while (buffer.Count > 0)
                    {
                        if (buffer[0] != ESC)
                        {
                            buffer.RemoveAt(0);
                            continue;
                        }

                        int frameLength = MeasureFrame(buffer);
                        if (frameLength <= 0)
                        {
                            break;
                        }

                        byte[] frame = new byte[frameLength];
                        buffer.CopyTo(0, frame, 0, frameLength);
                        buffer.RemoveRange(0, frameLength);

                        _owner.HandleFrame(this, frame);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Normal shutdown path.
            }
            catch (SocketException)
            {
                // The peer reset the connection, or the socket was closed underneath us.
                // Either way this session is finished; fall through to the finally block.
            }
            catch (IOException ex)
            {
                _owner.Log("WARN", "Session read failure: " + ex.Message);
            }
            catch (Exception ex)
            {
                _owner.Log("ERROR", "Session error: " + ex.Message);
            }
            finally
            {
                _owner.Log("CLOSE", "Client disconnected.");
                _owner.RemoveSession(this);
                Dispose();
            }
        }

        /// <summary>
        /// Determine the length of the frame at the head of <paramref name="buffer"/>.
        ///
        /// <para>
        /// <b>Why not just scan for the first 0x04?</b> A frame terminator search alone
        /// is unsafe: the payload of an <c>OE</c> frame declares its own length, and
        /// trusting the first <c>0x04</c> would let a stray terminator byte inside the
        /// payload split the frame early. This method cross-checks the declared length
        /// when one is present, and only falls back to the scanned terminator for the
        /// fixed-layout commands (print-signal setup, FIFO query, clear-queue) that
        /// carry no length field.
        /// </para>
        /// </summary>
        /// <returns>The frame length in bytes including the terminator, or 0 when no
        /// complete frame is available yet.</returns>
        private static int MeasureFrame(List<byte> buffer)
        {
            // A frame is at least "1B <cmd> ... 04" and every signature below reads up
            // to buffer[2], so a shorter buffer can never contain a complete frame.
            if (buffer.Count < 3 || buffer[0] != ESC)
            {
                return 0;
            }

            // Fixed-layout five-byte command with no payload.
            if (buffer.Count >= 5 && buffer[1] == 0x49 && buffer[2] == 0x31 && buffer[3] == 0x32)
            {
                return buffer[4] == EOT ? 5 : 0;
            }

            // OE family: 1B 4F 45 <4-digit length> <code text> 04.
            // The declared value is the length of the CODE TEXT only, so the frame is
            // 3 header + 4 length digits + declared + 1 terminator bytes long.
            if (buffer[1] == 0x4F && buffer[2] == 0x45)
            {
                int declared = -1;
                if (buffer.Count >= 8)
                {
                    int value = 0;
                    bool allDigits = true;

                    for (int i = 3; i <= 6; i++)
                    {
                        int digit = buffer[i] - 0x30;
                        if (digit < 0 || digit > 9)
                        {
                            allDigits = false;
                            break;
                        }

                        value = value * 10 + digit;
                    }

                    if (allDigits && value >= 1)
                    {
                        declared = value;
                    }
                }

                if (declared >= 0)
                {
                    int total = 3 + 4 + declared + 1;
                    if (buffer.Count < total)
                    {
                        return 0;
                    }

                    return buffer[total - 1] == EOT ? total : 0;
                }

                // Fixed-layout OE literals (FIFO query "00017", clear-queue "0000X"):
                // no usable length field, so the terminator is the frame boundary.
                int scanned = buffer.IndexOf(EOT);
                return scanned < 0 ? 0 : scanned + 1;
            }

            // Unknown command family: fall back to the first terminator.
            int terminator = buffer.IndexOf(EOT);
            return terminator < 0 ? 0 : terminator + 1;
        }

        /// <summary>Write bytes to the client.</summary>
        public void Send(byte[] data)
        {
            if (_closed)
            {
                return;
            }

            try
            {
                lock (_writeLock)
                {
                    _stream.Write(data, 0, data.Length);
                    _stream.Flush();
                }

                _owner.Log("TX", ToHex(data));
            }
            catch (Exception ex)
            {
                _owner.Log("WARN", "Write failed: " + ex.Message);
            }
        }

        /// <summary>Close the connection.</summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
                // Best-effort teardown.
            }

            try
            {
                _client.Close();
            }
            catch (Exception)
            {
                // Best-effort teardown.
            }
        }
    }
}

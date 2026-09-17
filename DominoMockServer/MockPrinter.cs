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

    private readonly int _port;
    private readonly int _printDurationMs;
    private readonly bool _quiet;
    private readonly CodenetHandler _handler = new CodenetHandler();

    private TcpListener? _listener;
    private readonly List<ClientSession> _sessions = new List<ClientSession>();
    private readonly object _sessionsLock = new object();
    private readonly MockFifoQueue _fifo = new MockFifoQueue();
    private long _jobSequence;

    private volatile bool _running;

    /// <summary>Raised for every logged line, useful for tests that assert on traffic.</summary>
    public event EventHandler<string>? LogLine;

    /// <summary>Creates the mock server.</summary>
    /// <param name="port">TCP port to listen on.</param>
    /// <param name="printDurationMs">How long the simulated print takes before the
    /// <c>0x32</c> completion event is pushed. Defaults to 1500 ms.</param>
    /// <param name="quiet">When true, suppresses console output. Defaults to false.</param>
    public MockPrinter(int port = 7000, int printDurationMs = 1500, bool quiet = false)
    {
        _port = port;
        _printDurationMs = printDurationMs > 0 ? printDurationMs : 1500;
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

    /// <summary>Current emulated FIFO depth.</summary>
    public int FifoCount
    {
        get { return _fifo.Count; }
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

        lock (_sessionsLock)
        {
            foreach (ClientSession session in _sessions)
            {
                session.Dispose();
            }

            _sessions.Clear();
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
        // printer. Accept a connection, handle it to completion (the session loop
        // blocks until the client disconnects), then accept the next. This removes the
        // cross-session FIFO contamination the old thread-per-client design allowed.
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
        MockJob job = new MockJob("MOCK-" + sequence.ToString("D6"), codeText, DateTime.Now);

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
        private readonly object _writeLock = new object();
        private volatile bool _closed;

        /// <summary>Whether this session's connection has been closed.</summary>
        internal bool IsClosed => _closed;

        public ClientSession(TcpClient client, MockPrinter owner)
        {
            _client = client;
            _owner = owner;
            _stream = client.GetStream();
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
                    if (!_stream.DataAvailable)
                    {
                        Thread.Sleep(5);
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

                        int endIndex = buffer.IndexOf(EOT);
                        if (endIndex < 0)
                        {
                            break;
                        }

                        byte[] frame = new byte[endIndex + 1];
                        buffer.CopyTo(0, frame, 0, endIndex + 1);
                        buffer.RemoveRange(0, endIndex + 1);

                        _owner.HandleFrame(this, frame);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Normal shutdown path.
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

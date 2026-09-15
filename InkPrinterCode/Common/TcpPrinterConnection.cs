using System.Net.Sockets;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Inkjet printer TCP connection implementation (added in round 2 of stage two)
    ///
    /// [CodeNet network conventions]
    ///   - The data port is fixed at 7000 (PDF section 2);
    ///   - Before connecting, a 4-byte reset command 07 01 01 2C may be sent to the "pre-reset port"
    ///     (field experience default 700), which is more reliable when stale cache remains. The PDF does not mention this;
    ///     it is field experience and is therefore made configurable (TcpResetEnabled / ResetPort=0 disables it).
    ///
    /// [Threading model]
    ///   - _ioLock protects the stream object: the receive thread's Read and the business thread's Write never touch the NetworkStream concurrently;
    ///     [2026-09-10] The Socket.Poll wait in Read has been moved outside that lock (wait outside the lock, read inside it),
    ///     so writers (code dispatch / heartbeat) hardly ever have to wait for the lock; see the Read method comments for details.
    ///   - Open / Close are only called from the start, reconnect and stop flows (single-threaded scenarios) and are not locked (locking could actually deadlock).
    ///     Close and Read therefore run concurrently without a lock: if the connection is closed during a wait, an ObjectDisposedException is thrown,
    ///     which the BLL catches and retries (existing design, not introduced here).
    /// </summary>
    public class TcpPrinterConnection : IPrinterConnection
    {
        /// <summary>Main connection timeout (ms) -- the device is on the local network, so 5 seconds is plenty</summary>
        private const int CONNECT_TIMEOUT_MS = 5000;

        private readonly string _host = string.Empty;
        private readonly int _port = 7000;
        private readonly bool _resetEnabled = false;
        private readonly int _resetPort = 0;

        private TcpClient? _client = null;
        private NetworkStream? _stream = null;

        /// <summary>Stream read/write lock (protects only the stream object; it does not serialise transactions)</summary>
        private readonly object _ioLock = new object();

        /// <summary>
        /// Construct the TCP connection
        /// </summary>
        /// <param name="host">Inkjet printer IP</param>
        /// <param name="port">Data port (fixed at 7000 for CodeNet)</param>
        /// <param name="resetEnabled">Whether to perform a pre-reset before the main connection</param>
        /// <param name="resetPort">Pre-reset port (0 = no reset)</param>
        public TcpPrinterConnection(string host, int port, bool resetEnabled, int resetPort)
        {
            _host = (host ?? string.Empty).Trim();
            _port = port;
            _resetEnabled = resetEnabled;
            _resetPort = resetPort;
        }

        /// <summary>Whether the connection is usable</summary>
        public bool IsConnected
        {
            get
            {
                TcpClient? client = _client;
                return client != null && client.Connected && _stream != null;
            }
        }

        /// <summary>
        /// Establish the connection: optionally pre-reset first, then connect to the data port
        /// [Failure behaviour] Any failing step throws, and any resources already acquired are released on the spot so no half-open connection is left behind.
        /// </summary>
        public void Open()
        {
            if (_host.Length == 0)
            {
                throw new ArgumentException("The TCP connection IP is empty. Please fill it in 'Printer Config' first.");
            }

            // ---------- 1. Pre-reset (field experience; failure does not block) ----------
            if (_resetEnabled && _resetPort > 0)
            {
                TryReset(_resetPort);
            }

            // ---------- 2. Main connection (with timeout, so the UI does not freeze when the site IP is unreachable) ----------
            TcpClient client = new TcpClient();

            try
            {
                IAsyncResult asyncResult = client.BeginConnect(_host, _port, null, null);

                if (!asyncResult.AsyncWaitHandle.WaitOne(CONNECT_TIMEOUT_MS))
                {
                    client.Close();
                    throw new TimeoutException("Timed out connecting to the inkjet printer (" + _host + ":" + _port.ToString()
                                               + ", no response for " + CONNECT_TIMEOUT_MS.ToString() + " ms).");
                }

                client.EndConnect(asyncResult);
                client.ReceiveTimeout = 5000;
                client.SendTimeout = CONNECT_TIMEOUT_MS;

                _client = client;
                _stream = client.GetStream();
            }
            catch
            {
                // Connection failed: release the resources newly created this time, then rethrow as-is (at this point
                // _client has not been assigned yet, so the old connection is unaffected)
                client.Close();
                throw;
            }
        }

        /// <summary>
        /// Send the 4-byte reset command (07 01 01 2C) to the pre-reset port
        /// [Boundary] A failed reset only produces Debug output -- a printer that does not support a reset port must not block a normal connection.
        /// </summary>
        private void TryReset(int resetPort)
        {
            try
            {
                using (TcpClient resetClient = new TcpClient())
                {
                    IAsyncResult asyncResult = resetClient.BeginConnect(_host, resetPort, null, null);

                    if (!asyncResult.AsyncWaitHandle.WaitOne(2000))
                    {
                        resetClient.Close();
                        System.Diagnostics.Debug.WriteLine("TCP pre-reset connection timed out (port " + resetPort.ToString() + "); skipping the reset and continuing to the main connection");
                        return;
                    }

                    resetClient.EndConnect(asyncResult);

                    using (NetworkStream resetStream = resetClient.GetStream())
                    {
                        byte[] resetFrame = CodeNetProtocol.BuildTcpResetFrame();
                        resetStream.Write(resetFrame, 0, resetFrame.Length);
                        resetStream.Flush();
                    }

                    // Wait a moment for the reset action to take effect, then disconnect the reset connection
                    // [2026-09-10] Hard-coded waits were uniformly reduced to 50 ms
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("TCP pre-reset failed (does not affect the main connection): " + ex.Message);
            }
        }

        /// <summary>Write out the raw bytes</summary>
        public void Write(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            lock (_ioLock)
            {
                NetworkStream? stream = _stream;
                if (stream == null)
                {
                    throw new InvalidOperationException("The TCP connection is not established; cannot send data.");
                }

                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
        }

        /// <summary>
        /// Read the raw bytes.
        ///
        /// [Why Poll is used instead of a direct Read] NetworkStream.Read is blocking; with no data it waits forever.
        ///   The receive thread is a resident loop that must be able to come back periodically to check the cancellation
        ///   signal, otherwise the stop flow's Join would hang forever.
        ///   Poll(timeout) is exactly a "wait with a timeout": it returns 0 on timeout, meaning there was no data this time.
        ///   [Note] Poll and heartbeat are two different things —— the heartbeat (send every 3 s / wait 2 s) checks
        ///   "is the peer still alive", while Poll answers "how does this machine's read return on time". When the
        ///   network cable is unplugged, this machine's socket receives no event at all and Poll simply keeps returning
        ///   "no data"; a silent loss of contact can only be discovered by the heartbeat. Neither can replace the other.
        ///
        /// [Why Poll must be outside the lock (2026-09-10, Mr. Wan's session, lock optimization wrap-up)]
        ///   Poll may block for the entire timeoutMs, and the connection lock is shared by Read / Write —— if the wait
        ///   were inside the lock, the reader would hold the lock for a long time and the writer (code sending /
        ///   heartbeat) would waste one Poll cycle waiting every single time.
        ///   It is split into three sections: (1) take the reference and null-check inside the lock (microseconds) →
        ///   (2) wait with Poll outside the lock → (3) actually read the data inside the lock.
        ///   Nothing touches the stream object during the wait, so no lock needs to be held; the writer's lock wait drops
        ///   to roughly zero.
        ///
        /// [The third section must re-fetch the field] The connection may be Closed while this method is waiting (Close
        ///   is not locked, and the startup / reconnect / stop flows all call it). Therefore the third section must not
        ///   reuse the local reference from the first section; it must re-fetch _stream from the field —— a null result
        ///   means the connection has been closed, and the exception thrown is caught by the BLL for a retry.
        ///
        /// [Disconnection semantics unchanged] Poll reporting readable while Available=0 is the standard signature of a
        /// TCP half-close → throw IOException.
        /// [Timeout semantics unchanged] Poll timeout → return 0, and the caller treats it as "no data this time".
        /// </summary>
        public int Read(byte[] buffer, int timeoutMs)
        {
            // ---------- Section 1 (inside the lock): take out the socket reference and null-check it; no blocking wait at all ----------
            // [2026-09-10] An initial value is mandatory: C#'s definite assignment analysis does not accept "a value
            //   assigned inside a lock block", so writing `Socket socket;` and then assigning inside the lock while using
            //   it outside would report CS0165 (use of unassigned local variable).
            Socket? socket = null;

            lock (_ioLock)
            {
                TcpClient? client = _client;

                if (client == null || _stream == null)
                {
                    throw new InvalidOperationException("The TCP connection is not established; cannot read data.");
                }

                socket = client.Client;
            }

            // Defensive null check (which also silences the nullability warning): a null check was already done above, so
            // this is not normally reached.
            if (socket == null)
            {
                throw new InvalidOperationException("The TCP connection is not established; cannot read data.");
            }

            // ---------- Section 2 (outside the lock): wait until readable ----------
            // This section blocks for at most timeoutMs and must never be executed inside the lock (it would hold up the writer).
            // If the connection is Closed during the wait, an ObjectDisposedException is thrown here and is handled by the
            // BLL's ObjectDisposedException branch (sleep and retry, waiting for the reconnect to swap in a new connection).
            bool readable = socket.Poll(timeoutMs * 1000, SelectMode.SelectRead);

            if (!readable)
            {
                return 0;
            }

            // ---------- Section 3 (inside the lock): actually read the data ----------
            // Re-fetch the stream and connection from the fields: the connection may have been Closed during the wait
            // (the field set to null and the stream Disposed), and reusing the local reference from section 1 would read
            // from an already-released object.
            lock (_ioLock)
            {
                NetworkStream? stream = _stream;
                TcpClient? client = _client;

                if (stream == null || client == null)
                {
                    throw new InvalidOperationException("The TCP connection is not established; cannot read data.");
                }

                // Poll says readable but there is no data to read = the peer has closed the connection (TCP half-close)
                if (client.Available == 0)
                {
                    throw new IOException("The inkjet printer has closed the TCP connection.");
                }

                return stream.Read(buffer, 0, buffer.Length);
            }
        }

        /// <summary>
        /// Close the connection. Safe to call repeatedly; does not throw.
        ///
        /// [2026-09-11] Close-method rework: graceful close + forced reset combined (Mr. Wan's session, to cure
        /// "inkjet printer connection count exhausted")
        /// [Background] The original Close() implementation only sent a FIN (graceful close), and whether the peer
        ///   released the connection was up to the inkjet printer's firmware —— on a real on-site machine, after
        ///   repeated reconnects it reported "maximum number of connections reached", proving that this firmware does
        ///   not actively release on receiving a FIN (or, in the heartbeat-timeout scenario, that the FIN never arrived
        ///   at all), so old connections kept piling up on the machine side and occupied slots.
        /// [Three-step close]
        ///   ① Shutdown(Both): politely tell the peer "I'm done talking" (send a FIN) —— preserving the notification semantics;
        ///   ② LingerOption(true, 0): mark the socket with SO_LINGER enabled + timeout 0 ——
        ///     this is documented Windows behavior: the subsequent Close makes the kernel send an RST and destroy the
        ///     socket immediately (this machine does not even enter TIME_WAIT);
        ///   ③ Close: send the RST according to the mark from ② —— an RST is a forced action at the peer's kernel level,
        ///     so on receiving it the inkjet printer's TCP stack unconditionally releases the connection slot
        ///     immediately, without depending on whether the firmware handles EOF.
        /// [Side effect] An RST discards data in this machine's send buffer that has not yet gone out. All four close
        ///   scenarios are harmless: heartbeat-timeout reconnect (the buffer content is discarded data anyway) /
        ///   write-failure reconnect (the code has already been marked as printed and is not resent) /
        ///   ending printing (the in-machine queue is going to be cleared anyway) / program exit (same as before).
        /// [Ordering constraint] Shutdown / setting LingerState must come before disposing the stream —— NetworkStream's
        ///   Dispose releases the socket along with it, after which it can no longer be set. Each of the two steps has its
        ///   own try/catch: when the connection is already dead Shutdown throws SocketException, and when it has already
        ///   been released it throws ObjectDisposedException; neither may affect the subsequent release actions
        ///   (following the DominoTcpClient.SafeClose structure provided by Mr. Wan).
        /// </summary>
        public void Close()
        {
            try
            {
                NetworkStream? stream = _stream;
                _stream = null;

                TcpClient? client = _client;
                _client = null;

                // ---------- (1) (2) (3): the three-step close (performed only on a socket that actually exists) ----------
                if (client != null)
                {
                    Socket? socket = client.Client;

                    if (socket != null)
                    {
                        // (1) Polite notification: Shutdown(Both) sends a FIN; it throws when the connection is already dead, so ignore that
                        try
                        {
                            if (socket.Connected)
                            {
                                socket.Shutdown(SocketShutdown.Both);
                            }
                        }
                        catch (Exception)
                        {
                            // Already disconnected / already released; no notification needed
                        }

                        // (2) Set the RST mark: Linger(true, 0) —— the subsequent Close then makes the kernel send an RST
                        //    rather than a FIN; if some platform state is abnormal this may throw, and ignoring it degrades
                        //    to an ordinary FIN close (no worse than before)
                        try
                        {
                            socket.LingerState = new LingerOption(true, 0);
                        }
                        catch (Exception)
                        {
                            // If setting it fails, fall back to the original graceful close; the behavior is no worse than before the rework
                        }
                    }
                }

                // (3) Close: send the RST according to the mark from (2) (naturally safe here when the socket has already been released by stream.Dispose)
                if (stream != null)
                {
                    stream.Dispose();
                }
                if (client != null)
                {
                    client.Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Exception while closing the TCP connection (ignored): " + ex.Message);
            }
        }

        /// <summary>Release resources (equivalent to Close)</summary>
        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }
    }
}

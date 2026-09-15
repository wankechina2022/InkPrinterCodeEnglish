using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-12] Inkjet printing service state machine (timed code sending v3 + code-claim occupancy model + stash resend)
    ///
    /// [Iron rule against duplicates (2026-09-10 17:29, highest priority · code-claim occupancy model)]
    ///   The instant a code is taken out of the database it is immediately marked as "printed" (occupancy lock), and only afterwards
    ///   is it written to the inkjet printer — the re-claim condition is always PrintStatus=0, so the same code can never be taken
    ///   out a second time, and no exceptional path (write failure / database exception / lost response) can cause a duplicate print.
    ///   [No rollback on failure] A write failure still leaves the code as printed (better to miss a print than to print twice);
    ///   stopping does not roll back any code.
    /// [2026-09-12] stash resend is the single exception to the iron rule: a code that was not confirmed as accepted
    ///   (0x15 / timeout / write failure) is resent as "the same code", not "claim a second code and write that" — the semantics
    ///   of the iron rule against duplicates are unchanged.
    ///
    /// [Thread structure (all IsBackground=true, so program exit is never blocked)]
    ///   Startup thread  : connect → spin up the resident threads → set signals → clear the three queues → prefill the cache (one-off)
    ///   Receive thread  : the only entry point that reads the stream; routes by frame characteristics (guards against packet sticking),
    ///                     delivers ACKs / triggers print-done / resets the "consecutive ack timeout" counter (receiving any byte from
    ///                     the machine proves the peer is alive)
    /// Heartbeat thread: [disabled] the code is retained but no longer started (2026-09-12) —
    ///                     the 200ms timed code send is itself a "write + wait for response" liveness probe, far denser than a 3-second heartbeat
    ///   Send thread     : sends one record every 200ms (SEND_INTERVAL_MS): if stash is non-empty, resend the stash code;
    /// otherwise claim a new code (2026-09-12; supersedes the original "0x32-driven refill")
    ///   Reconnect thread: after a disconnect, reconnect at the configured interval; on success re-initialize the protocol environment
    ///                     and top up the cache (stash is preserved)
    ///   Stop thread     : the seven-step stop sequence (timers → threads → clear queues → close connection → release → restore UI),
    ///                     clearing stash
    ///   Test print thread: opens a separate connection → sets signals → clears the three queues → sends AB12345 → closes and releases immediately
    /// (only available in the "Not Started" state; mutually exclusive with start/stop printing; 19:53)
    ///
    /// [Two-level locking responsibilities]
    ///   _ioLock : business-layer transaction lock — wraps the single "send" operation (writing bytes); waiting for the response happens
    /// outside the lock, so heartbeat / code send / test do not block each other (13:42);
    ///   Connection-layer internal lock: only protects the stream object from concurrent access (see the IPrinterConnection interface comments).
    ///
    /// [Packet-sticking protection]
    ///   The receive thread feeds the byte stream into _rxBuffer and splits it by frame characteristics: 0x06/0x15/0x32 are single-byte
    ///   events; a complete frame starting with 0x1B is parsed by pairing up to the frame terminator 0x04; bytes that do not match are
    ///   written to the log verbatim in hexadecimal.
    ///   _ioLock guarantees that only one "write" is in flight at any moment, and the 200ms cadence is naturally serial, so response
    ///   ownership is unambiguous.
    ///
    /// [Timed code sending (2026-09-12, design v3; supersedes the original "0x32-driven refill")]
    ///   The send thread sends one record every SEND_INTERVAL_MS (200ms, static constant):
    ///   · stash (_stashCode) is non-empty → resend the code in stash (no re-claiming, no re-occupying, no double counting);
    ///   · stash is empty → claim a new code in Id order; claiming marks it printed immediately (occupancy model).
    ///   [Two-branch decision] The inkjet printer replies 0x06 = accepted → next round claims a new code;
    ///   anything else (0x15 reject / response timeout / write failure) → that code goes into stash and the same code is resent next round.
    ///   0x32 print-done is still received as usual, displayed as usual and counted into "printed count" as usual, but no longer drives code sending.
    /// [Send count semantics] Only a successful new-code claim increments it; stash resends are not counted (resends must never be double counted).
    ///   [Re-print boundary] If the machine has already accepted but the 0x06 is lost/late, a resend will re-print the same code — under TCP's
    /// reliable transport the probability is extremely low, and a network drop + reconnect clears the queue as a backstop;
    ///   "not burning codes (not wasting codes) takes priority" (2026-09-12).
    ///
    /// [Disconnect detection (replaces the disabled heartbeat, 2026-09-12)]
    ///   After each code send we wait for the response; SEND_ACK_TIMEOUT_STREAK_MAX consecutive response timeouts (i.e. "we wrote it but got
    ///   no feedback at all") → judged offline → enter the reconnect flow; the receive thread resets the counter immediately upon receiving
    ///   any byte from the machine.
    ///
    /// [Test print (fully independent; 19:53)]
    ///   Shares the same configuration as production, but opens a separate connection object: connect → set the print signal → clear the three
    ///   queues → send the fixed value AB12345 → close and release the connection. It does not write to the database and does not consume a
    ///   production code Id.
    ///   Production must already be stopped during a test (guaranteed by mutual exclusion), so clearing the queues cannot affect any in-flight
    ///   production data.
    ///   [Release iron rule] Regardless of success / reject / timeout / exception, finally must fully release via Close() + Dispose();
    ///   program exit (StopSync) also joins the test thread and closes that test connection.
    ///
    /// [Logging strategy (19:47 / 19:53 / 2026-09-12)]
    ///   To disk (LogHelper): all read/write exceptions, protocol exceptions, business and state-machine events — evidence for
    ///     troubleshooting; 0x15 rejects are completely silent in the UI, and the on-disk log is rate-limited to a 30-second summary
    ///     ("cache full ongoing, N rejects so far") to avoid flooding the disk.
    ///   UI only (LogDevice): the device interaction process — 0x06 responses / 0x32 print-done / code sends / successful commands /
    ///     all test print lines. The operator can see the "receive feedback → send next code" cycle clearly from this.
    /// </summary>
    public class PrintServiceBLL
    {
        // ============================================================
        // Events to the UI (all triggered on background threads; the UI side must Invoke back to the UI thread)
        // ============================================================

        /// <summary>One line of the run log (no timestamp; the UI adds the time itself and displays in reverse order)</summary>
        public event Action<string>? RunLog;

        /// <summary>Production state change: (state text, whether running). The UI uses this to drive the three buttons</summary>
        public event Action<string, bool>? ServiceStateChanged;

        /// <summary>Inkjet printer state change: (text, color) — Disconnected / Running (green) / Offline (red)</summary>
        public event Action<string, System.Drawing.Color>? PrinterStateChanged;

        /// <summary>Dashboard number change (triggered after a successful code send; the UI re-queries the database to refresh)</summary>
        public event Action? DashboardChanged;

        // ============================================================
        // Internal state
        // ============================================================

        /// <summary>
        /// ACK wait transaction (after a code send / command write, the receive thread delivers the result).
        /// [2026-09-10] Changed to a three-state form after the lock-hold rework (22:33):
        ///   Received=false → the response has not arrived yet (a wait timeout returns null based on this, distinguishing it from "0x15 received");
        ///   Received=true  → received; only then does Acked carry meaning (true = 0x06 / false = 0x15).
        /// [Why three states are necessary] Originally there was only a single bool Acked, so "timed out with nothing received" and "received 0x15"
        ///   both looked like false and could not be distinguished; the new wait-outside-the-lock logic requires "check whether it was received
        ///   first, then decide whether to sleep", so expressing "not received" is mandatory.
        /// </summary>
        private class PendingAck
        {
            /// <summary>Whether a response has been received (false = the receive thread has not delivered any result yet)</summary>
            public bool Received = false;

            /// <summary>true = 0x06; false = 0x15 (only meaningful when Received is true)</summary>
            public bool Acked = false;
        }

        /// <summary>
        /// [2026-09-12] Result of sending a single code (the basis for the two-branch decision of timed code sending v3)
        /// [Acked]       The inkjet printer replied 0x06, confirming acceptance → next round claims a new code (stash is cleared)
        /// [Nacked]      The inkjet printer replied 0x15 rejecting it (usually the internal cache is full) → that code goes into stash, and the same code is resent next round
        /// [AckTimeout]  Response timeout (written out but no feedback at all) → that code goes into stash and is resent; 3 consecutive times is judged offline
        /// [ClaimFailed] Claim failed (database exception, or the code was already marked by another path) → this record was never written out;
        ///               the code was not occupied by this round, so it does not go into stash, and the next round's re-claim retries it naturally (anti-duplicate takes priority)
        /// [WriteFailed] Write exception (the connection has a problem) → that code goes into stash and is resent next round; also triggers a reconnect
        /// </summary>
        private enum SendResult
        {
            Acked = 0,
            Nacked = 1,
            AckTimeout = 2,
            ClaimFailed = 3,
            WriteFailed = 4
        }

        /// <summary>
        /// [2026-09-12] * Timed code-send cadence (milliseconds) * — kept as a static variable so future code
        /// changes only need to look in one place.
        /// The send thread sends one code every SEND_INTERVAL_MS: if stash is non-empty it resends the stash code, otherwise it claims a new code.
        /// [Value rationale] 200ms = at most 5 records per second, which is natural rate limiting (when the machine cache is full, extra sends only
        ///   get a 0x15; rate limiting prevents high-frequency spinning); if the production line is faster than 5 bottles/second, just lower this
        ///   value and recompile.
        /// </summary>
        private const int SEND_INTERVAL_MS = 200;

        /// <summary>
        /// [2026-09-12] Response timeout for a production code send (milliseconds); changed from 100 to 2000 this time.
        /// [Semantics (timed code sending v3)] Timeout = "the code was written out but there was no feedback at all": that code goes into stash and
        ///   is resent next round (without wasting the code), and SEND_ACK_TIMEOUT_STREAK_MAX consecutive timeouts judges the link offline and switches
        ///   to reconnect — since the heartbeat is disabled, this value directly determines how fast a disconnect is detected:
        ///   3 × (2000 + 200) ≈ 6.6 seconds.
        /// [Why give the full 2000ms] The cost of a false timeout is a stash resend — if the machine actually accepted the code (the 0x06 was merely in
        ///   flight), the resend would re-print the same code. A normal response arrives within tens of milliseconds, so 2000ms is ample margin; better slow
        ///   than wrong.
        /// [History] 100ms was set for the old plan B' (from the era when waiting happened inside _ioLock) to compress lock hold time; after the lock
        ///   optimization the wait is outside the lock, and under the new mechanism a timeout triggers a stash resend, so enough margin must be given to
        ///   prevent false positives.
        /// [Not applicable] The startup/reconnect signal setup and queue clearing (ExecInstruction) still use SendResponseTimeoutMs; those commands must be
        ///   given ample response time and are unaffected by this constant.
        /// </summary>
        private const int SEND_ACK_TIMEOUT_MS = 2000;

        /// <summary>
        /// [2026-09-10] Wait timeout for a single Read on the receive thread (milliseconds).
        ///
        /// [Current semantics (after Socket.Poll was moved out of the connection lock)] This value only determines "how often the receive thread wakes up when
        ///   there is no data". It affects two things: first, cancellation responsiveness (the worst-case wait when the stop sequence joins the receive thread);
        ///   second, the idle spin frequency. **It no longer affects the writer's (code send / heartbeat) lock wait time** — the connection layer's Poll has
        ///   been moved outside the lock, so the reader only holds the lock for "fetch the reference + actually read data", which is microsecond-level.
        ///
        /// [History] 200 → 50 → 15: early on, Poll was inside the connection lock, so the reader's lock hold time equaled the writer's worst-case wait; therefore
        /// on 2026-09-10 this value was tightened from 50 to 15 to compress the writer's wait. On the same day at 18:35, Poll was moved out of the lock,
        ///   after which the writer's wait dropped to zero and this value became completely decoupled from lock contention; 15 is retained only to make
        ///   cancellation more responsive.
        ///
        /// [Why not push it lower] The Windows system timer granularity is about 15.6ms, and the actual minimum wait of Socket.Poll / Thread.Sleep is limited by
        ///   that — setting 10 or 5 is essentially ineffective and still takes about 15.6ms to return.
        ///
        /// [Cost] With no data, the receive thread spins at about 64 times per second. Poll is a kernel blocking wait that consumes no CPU computation time,
        ///   so the overhead is negligible.
        /// </summary>
        private const int RECEIVE_POLL_TIMEOUT_MS = 15;

        private readonly object _stateLock = new object();
        private ServiceState _state = ServiceState.Stopped;

        /// <summary>Current connection (volatile: read by the receive thread, swapped by the reconnect thread)</summary>
        private volatile IPrinterConnection? _connection;

        /// <summary>
        /// Business-layer transaction lock: wraps the single "send" operation (writing bytes).
        /// [2026-09-10] After the lock rework it **no longer wraps "wait for response"** — the wait has been moved outside the lock (see WaitPendingAck), so the lock
        ///   hold time is only the microsecond-level byte write, and readers/writers no longer block each other.
        /// </summary>
        private readonly object _ioLock = new object();

        /// <summary>ACK delivery lock: receive thread → the waiting transaction (protects hanging/delivering/removing _pendingAck)</summary>
        private readonly object _ackLock = new object();

        /// <summary>Inbox of the currently in-flight transaction (at most one at a time; see the BeginPendingAck comments for the concurrency preconditions)</summary>
        private PendingAck? _pendingAck = null;

        /// <summary>Receive buffer (written by the receive thread, parsed within the same lock)</summary>
        private readonly object _rxLock = new object();
        private readonly List<byte> _rxBuffer = new List<byte>();

        /// <summary>
        /// [2026-09-12] Code pending resend (stash): the code that was sent last time but not confirmed as accepted by the inkjet printer
        /// (0x15 / timeout / write failure).
        /// [Semantics (2026-09-12, timed code sending v3)] Code sending fires once every 200ms: if stash is non-empty → resend this code in stash
        ///   (no re-claiming, no re-occupying, send count not incremented); if stash is empty → claim a new code.
        ///   Two-branch decision: the machine replies 0x06 = accepted → stash is cleared and a new code is claimed next round;
        ///   0x15 / response timeout / write failure → the code stays in stash (or the new code is placed there) and the same code is resent next round.
        /// [Whole CodeData is stored] The resend log needs the Id for easy reconciliation with the database (the old quota mechanism _pendingSend was removed entirely).
        /// [Lifecycle] Cleared on startup; preserved across reconnects (after a reconnect the stash code queues behind the 3 prefilled records waiting for a slot;
        /// being a few beats later loses nothing); cleared on stop (burning 1 code on shutdown is acceptable).
        /// [Access] Only the send thread and the prefill reads/writes of the startup/reconnect thread (during prefill the send thread is blocked by a latch, so
        ///   there is at most one writer at a time); reference assignment itself is atomic, so no lock is needed.
        /// </summary>
        private CodeData? _stashCode = null;

        /// <summary>
        /// [2026-09-12] Consecutive "code-send response timeout" counter (the basis for disconnect detection after the heartbeat was disabled).
        /// [Mechanism] The send thread increments it on every response timeout; the receive thread resets it to zero immediately upon receiving any byte from the
        ///   machine (being able to send bytes means the peer is alive); reaching SEND_ACK_TIMEOUT_STREAK_MAX judges the link offline and switches to reconnect.
        /// [Why only the receive thread resets it] With a TCP half-open link (cable unplugged / peer crashed) Write still "succeeds"; only actually reading bytes
        ///   can prove the peer is alive — the same logic as the original heartbeat's "trust reads, not writes".
        /// [Access] Interlocked (incremented by the send thread, reset by the receive thread — two threads).
        /// </summary>
        private int _ackTimeoutStreak = 0;

        /// <summary>Threshold of consecutive response timeouts for judging the link offline (2026-09-12: no feedback after 3 consecutive writes means the network is down)</summary>
        private const int SEND_ACK_TIMEOUT_STREAK_MAX = 3;

        /// <summary>Prefill count (how many records are sent once at startup/reconnect; no longer used as a send gate during operation)</summary>
        private int _targetCache = 3;

        /// <summary>Inkjet printer online flag (set false on heartbeat timeout, true when reconnect succeeds)</summary>
        private volatile bool _printerOnline = false;

        /// <summary>Reconnect mutex flag: 0 = idle, 1 = reconnecting (prevents the heartbeat and code send from spawning two reconnect threads at once)</summary>
        private int _reconnectFlag = 0;

        /// <summary>Test print mutex flag: 0 = idle, 1 = executing (prevents double clicks)</summary>
        private int _testFlag = 0;

        /// <summary>
        /// [2026-09-10] The moment of the last successful "read data" (Environment.TickCount64, monotonically increasing, unaffected by system clock changes).
        ///
        /// [Trust reads only, not writes (19:53)] With a TCP half-open link / cable unplugged, Write still returns success (the data only made
        ///   it into the local kernel send buffer, and nothing proves the peer received it). If a successful write were also counted, then when "the device is
        ///   dead but the socket is still hanging" this value would keep being refreshed → the heartbeat would never run → a disconnect would never be detected.
        ///   Only actually reading bytes proves the peer is still alive.
        ///
        /// [Purpose] The heartbeat thread uses this to skip liveness probing: if idle &lt; heartbeat interval → there was just data interaction, so no query frame
        ///   is needed this round. During continuous production every code definitely gets a 0x06 + 0x32 reply, so reads keep producing data → the heartbeat
        /// essentially never runs (by design).
        ///
        /// [Access] Writes go through Interlocked.Exchange and reads through Interlocked.CompareExchange(...,0,0), so it is visible across threads.
        /// </summary>
        private static long _lastIoMs = 0;

        /// <summary>Whether "no usable code in the database" has already been reported (report only once to avoid flooding the screen)</summary>
        private bool _noCodeLogged = false;

        /// <summary>
        /// [2026-09-10] "Send count" for this run — incremented on every trip through Write, regardless of whether the write succeeded and ignoring any feedback.
        /// [Semantics (20:23)] Bounded by "the dispatch action of this run": merely entering the write branch counts, and a failed write
        ///   or a 0x15 reject counts just the same; it is used to compare against the "printed count" to reveal the in-flight quantity of "sent but not yet printed"
        /// (a discrepancy is not a problem).
        /// [Reset] Reset to zero on every Start(); a reconnect does not reset it (it is still the same run).
        /// [Access] Incremented with Interlocked on background threads, read atomically by the UI thread via the RunSendCount property (CompareExchange).
        /// </summary>
        private int _runSendCount = 0;

        /// <summary>
        /// [2026-09-10] "Printed count" for this run — only the 0x32 (print done) sent back by the inkjet printer counts; nothing else does.
        /// [Semantics (20:23)] Failed writes / 0x15 rejects / prefilled records not yet printed by the machine are all excluded.
        /// [Reset] Reset to zero on every Start(); a reconnect does not reset it (it is still the same run).
        /// [Access] Incremented with Interlocked by the receive thread, read atomically by the UI thread via the RunPrintedCount property (CompareExchange).
        /// </summary>
        private int _runPrintedCount = 0;

        /// <summary>
        /// [2026-09-11] "Available code base" (decoupling the dashboard from database queries, D1).
        /// [Mechanism] On every startup, before PrefillCache, query the total not-printed count once and lock it in as the base; during operation the dashboard's
        ///   available count = base − send count (RunSendCount), with no further database queries.
        ///   Rationale: in SendOneCode a successful MarkPrinted is always accompanied by send count +1, and on failure neither side moves, so the decrease in
        ///   not-printed stock is identically equal to the send count, record by record, and the subtraction yields the true available count in the database.
        /// [Value semantics] −1 = unknown (not yet locked / the query failed at startup); the UI falls back to a live database query in that case.
        /// [Reset] Set to −1 on every Start(); a reconnect does not re-take it (a reconnect refill likewise moves both sides in sync — "not printed −3, send +3" —
        ///   so the identity holds; re-taking the base on reconnect would instead double-count against the send count).
        /// [Access] Written with Interlocked by the startup thread, read atomically by the UI thread via the AvailableBase property (CompareExchange).
        /// </summary>
        private int _availableBase = -1;

        /// <summary>Last recorded inkjet printer status code (log only when the status code changes, to avoid one line every 3 seconds)</summary>
        private int _lastLoggedStatusCode = -1;

        /// <summary>Status frame arrival signal (waited on by the heartbeat thread)</summary>
        private readonly AutoResetEvent _statusFrameEvent = new AutoResetEvent(false);

        /// <summary>
        /// Print-done event signal.
        /// [2026-09-12] Code sending is now driven by the 200ms timer, so the send thread no longer waits on this signal (OnPrintDone still Sets it);
        ///   the field is retained so it is directly usable if event-driven operation is restored.
        /// </summary>
        private readonly AutoResetEvent _printDoneEvent = new AutoResetEvent(false);

        /// <summary>Cancellation signal (set in the first step of the stop sequence)</summary>
        private CancellationTokenSource? _cts = null;

        // Resident thread references (used by the stop sequence to Join)
        private Thread? _receiveThread = null;
        private Thread? _heartbeatThread = null;
        private Thread? _sendThread = null;
        private Thread? _reconnectThread = null;
        private Thread? _workerThread = null;

        /// <summary>[2026-09-10] Test print thread reference (joined on program exit to ensure the test connection is cleaned up)</summary>
        private Thread? _testPrintThread = null;

        /// <summary>
        /// [2026-09-10] Separate connection object for test printing (fully separate from the production connection _connection).
        /// Used by the stop sequence / program exit as a backstop close; the test thread's own finally also closes it, and closing twice is safe.
        /// </summary>
        private IPrinterConnection? _testConnection = null;

        /// <summary>Service state enum</summary>
        public enum ServiceState
        {
            /// <summary>Not started (the only state in which "Start Printing" may be clicked)</summary>
            Stopped = 0,
            /// <summary>Starting (during connect/prefill, all buttons locked)</summary>
            Starting = 1,
            /// <summary>Running (Stop Printing available; Test Print unavailable)</summary>
            Running = 2,
            /// <summary>Stopping (while the stop sequence is executing, all buttons locked)</summary>
            Stopping = 3,
            /// <summary>Test printing (separate connection; mutually exclusive with start/stop printing, all buttons locked)</summary>
            Testing = 4
        }

        // ============================================================
        // Public interface
        // ============================================================

        /// <summary>Whether it is currently running (including during a reconnect — the service is still running, only the connection is down)</summary>
        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                {
                    return _state == ServiceState.Running;
                }
            }
        }

        /// <summary>
        /// "Send count" for this run (incremented on every trip through Write, regardless of success and ignoring feedback), used for the dashboard
        /// display (atomic read).
        /// Semantics: see the _runSendCount field description.
        /// </summary>
        public int RunSendCount
        {
            get
            {
                return System.Threading.Interlocked.CompareExchange(ref _runSendCount, 0, 0);
            }
        }

        /// <summary>
        /// "Printed count" for this run (only the 0x32 sent back by the machine counts), used for the dashboard display (atomic read).
        /// Semantics: see the _runPrintedCount field description.
        /// </summary>
        public int RunPrintedCount
        {
            get
            {
                return System.Threading.Interlocked.CompareExchange(ref _runPrintedCount, 0, 0);
            }
        }

        /// <summary>
        /// Available code base (−1 = unknown), used for the dashboard display (atomic read). Semantics: see the _availableBase field description.
        /// </summary>
        public int AvailableBase
        {
            get
            {
                return System.Threading.Interlocked.CompareExchange(ref _availableBase, -1, -1);
            }
        }

        /// <summary>
        /// Start printing (btnStart / btnStop are mutually exclusive, guaranteed by the UI event wiring)
        /// [Mechanism] Only spins up the startup thread and does not block the UI — on connect/prefill failure it goes back to "Not Started" and pops a log explanation.
        /// </summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_state != ServiceState.Stopped)
                {
                    return;
                }
                _state = ServiceState.Starting;
            }

            // [2026-09-10] The dashboard's two counters start from 0 on every start
            //   ("send count" and "printed count"; a reconnect does not reset them, as it is still the same run)
            // [2026-09-11] The available code base is also set to −1 (unknown): the base is re-locked by the startup thread in StartSequence
            //   before prefilling, sharing the same starting point as the send count, so that "base − send count" is consistent.
            System.Threading.Interlocked.Exchange(ref _runSendCount, 0);
            System.Threading.Interlocked.Exchange(ref _runPrintedCount, 0);
            System.Threading.Interlocked.Exchange(ref _availableBase, -1);

            FireServiceState("Starting", false);
            LogInfo("[Print service] Starting...");

            _workerThread = new Thread(StartSequence);
            _workerThread.IsBackground = true;
            _workerThread.Name = "PrintService-Start";
            _workerThread.Start();
        }

        /// <summary>
        /// Stop printing (the seven-step stop sequence runs on a background thread; the UI restores itself upon receiving the "Not Started" event)
        /// </summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                if (_state != ServiceState.Running && _state != ServiceState.Starting)
                {
                    return;
                }
                _state = ServiceState.Stopping;
            }

            FireServiceState("Stopping", false);
            LogInfo("[Print service] Stopping...");

            _workerThread = new Thread(delegate () { StopSteps("Manual stop"); });
            _workerThread.IsBackground = true;
            _workerThread.Name = "PrintService-Stop";
            _workerThread.Start();
        }

        /// <summary>
        /// Synchronous stop (for program exit only): runs the stop sequence directly on the calling thread to ensure all connections are closed before exit.
        /// [Edge case] Called when MainForm closes; each step has a timeout, so it returns within a few seconds in the worst case.
        /// </summary>
        public void StopSync()
        {
            lock (_stateLock)
            {
                if (_state == ServiceState.Stopped)
                {
                    return;
                }
                _state = ServiceState.Stopping;
            }

            StopSteps("Program exit");

            lock (_stateLock)
            {
                _state = ServiceState.Stopped;
            }
            FireServiceState("Not Started", false);
        }

        /// <summary>
        /// Test print (fully independent, only available in the "Not Started" state; 19:53).
        /// [Mutual exclusion] Start printing can only be initiated from the not-started state, and stop printing only from the running/starting state, while this
        ///   method only lets the call through in the "Not Started" state → test vs. start and test vs. stop are naturally mutually exclusive (the UI layer also
        /// disables the button, as a safety net).
        /// [Flow] Only sets the state and spins up the test thread; the actual connect / send / release all happen inside TestPrintSequence.
        /// </summary>
        public void TestPrint()
        {
            // A test is only allowed when completely idle (not started): running / starting / stopping / testing are all rejected
            lock (_stateLock)
            {
                if (_state != ServiceState.Stopped)
                {
                    LogDevice("[Test print] The current state does not allow a test (please click \"Stop Printing\" first to return to Not Started, then try again).");
                    return;
                }
                _state = ServiceState.Testing;
            }

            // Mutex against double clicks: if a test is already running, ignore this click
            if (System.Threading.Interlocked.CompareExchange(ref _testFlag, 1, 0) != 0)
            {
                lock (_stateLock)
                {
                    _state = ServiceState.Stopped;
                }
                return;
            }

            FireServiceState("Testing", false);

            _testPrintThread = new Thread(TestPrintSequence);
            _testPrintThread.IsBackground = true;
            _testPrintThread.Name = "PrintService-TestPrint";
            _testPrintThread.Start();
        }

        // ============================================================
        // Startup sequence (executed by the startup thread)
        // ============================================================

        /// <summary>
        /// Startup sequence: read configuration → create connection → protocol initialization → spin up the resident threads → prefill the cache → running.
        /// If any step fails: run the stop sequence to reclaim resources and return the state to "Not Started".
        /// </summary>
        private void StartSequence()
        {
            try
            {
                _cts = new CancellationTokenSource();
                // [2026-09-12] Timed code sending v3: stash and the consecutive-timeout counter reset to zero on startup (the old quota ResetPendingSend has been removed)
                _stashCode = null;
                System.Threading.Interlocked.Exchange(ref _ackTimeoutStreak, 0);
                _targetCache = ConfigHelper.InitialCacheCount;
                _printerOnline = false;
                _noCodeLogged = false;
                _lastLoggedStatusCode = -1;

                // ---------- 1. Read the enabled configuration ----------
                PrinterConfigBLL.LoadResult load = PrinterConfigBLL.Load();
                if (!load.Success)
                {
                    throw new InvalidOperationException(load.Message);
                }

                // ---------- 2. Create the connection ----------
                _connection = CreateConnection(load);
                _connection.Open();
                LogInfo("[Connect] Established: " + DescribeConnection(load));

                // ---------- 3. Spin up the resident threads ----------
                // [2026-09-10] P2 fix: the threads must be started before protocol initialization — otherwise, after the 4 commands below are
                //   sent, there would be no receive thread reading the stream, and each command would waste the full SendResponseTimeoutMs
                //   (3 seconds by default), making startup consistently about 12 seconds slow and producing misleading "response timeout" log lines.
                //   Safety: the heartbeat/send thread loops have a double latch of IsRunning (false during Starting) and _printerOnline
                //   (false before startup succeeds), so once started they only sleep and will not send any data ahead of prefill completion.
                StartThreads();

                // ---------- 4. Protocol environment initialization: print signal setup + clear the three cache queues ----------
                // [Thread ownership] This step and the prefill code send below both run sequentially on the startup thread (PrintService-Start), and
                //   together with "send 3 codes in a row" they are two consecutive segments of the same thread, naturally serial with no contention;
                //   the heartbeat/send resident threads are blocked at the loop entrance by the double latch at this moment and will not send any data.
                ExecInstruction(CodeNetProtocol.BuildSignalSetupFrame(), "Print signal setup");
                ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(0), "Clear TCP/IP cache queue");
                ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(1), "Clear RS232 cache queue");
                ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(2), "Clear history cache queue");

                // ---------- 4.5 Lock in the "available code base" (decoupling the dashboard from database queries, D1; 2026-09-11) ----------
                // [Mechanism] At startup, query the total not-printed count once as the base; afterwards the running dashboard's available count = base − send count,
                //   and not a single database query is made during operation (with tens of millions of rows the UI is no longer slowed down by COUNT).
                // [Placement] Must be between this step (4.5) and step 5 PrefillCache() — prefilling claims codes, which moves "not printed −3, send count +3"
                //   (both sides move in sync, so the identity "not-printed decrease ≡ send count" holds), but if the base were taken after the prefill it would
                //   double-deduct those 3 prefilled records, leaving the available count falsely 3 lower long-term.
                // [Failure] A failed base query only sets −1 (the UI falls back to live database queries); it must never abort the startup.
                try
                {
                    int baseCount = CodeDataDAL.GetStatusCount(PrintStatus.NotPrinted);
                    System.Threading.Interlocked.Exchange(ref _availableBase, baseCount);
                    LogInfo("[Dashboard] Available code base locked in: " + baseCount.ToString()
                            + " records; during operation it switches to \"base − send count\" subtraction display, no more database queries");
                }
                catch (Exception ex)
                {
                    System.Threading.Interlocked.Exchange(ref _availableBase, -1);
                    LogWarn("[Dashboard] Failed to obtain the available code base; falling back to live database queries during operation: " + ex.Message);
                }

                // ---------- 5. Prefill the cache (claim codes in Id order; claiming marks them printed, occupancy model) ----------
                // [2026-09-10] P3-2 note: prefill and the send thread will not concurrently claim the same not-printed code —
                //   this relies on the double latch above: during prefill _state=Starting (IsRunning=false) and
                //   _printerOnline=false, so the send thread is blocked right at the loop entrance; the running state is entered
                //   only after the prefill has fully finished.
                //   If the step order of this method is ever adjusted, be sure those two latches are still closed before PrefillCache.
                int prefilled = PrefillCache();

                // ---------- 6. Enter the running state ----------
                lock (_stateLock)
                {
                    _state = ServiceState.Running;
                }
                _printerOnline = true;

                FirePrinterState("Running", System.Drawing.Color.Green);
                FireServiceState("Running", true);
                LogInfo("[Print service] Started successfully: prefilled " + prefilled.ToString()
                        + " records; from now on one code is sent every " + SEND_INTERVAL_MS.ToString()
                        + " milliseconds, the next code is sent as soon as 0x06 accepts it, and the same code is resent on 0x15/timeout");
            }
            catch (Exception ex)
            {
                LogError("[Print service] Startup failed: " + ex.Message);
                LogHelper.Instance.Error("Inkjet printing service startup failed", ex);

                // Reclaim resources and return to "Not Started" so the configuration can be adjusted and a fresh start made
                StopSteps("Startup failure rollback");
            }
        }

        /// <summary>Create the connection object from the enabled configuration (shared by startup and reconnect; a reconnect reads the latest configuration)</summary>
        private IPrinterConnection CreateConnection(PrinterConfigBLL.LoadResult load)
        {
            if (load.EnabledType == Model.Enums.ConnType.Tcp)
            {
                bool resetEnabled = ConfigHelper.TcpResetEnabled && load.TcpConfig.ResetPort > 0;
                return new TcpPrinterConnection(load.TcpConfig.TcpIp, load.TcpConfig.TcpPort,
                                                resetEnabled, load.TcpConfig.ResetPort);
            }
            else
            {
                return new SerialPrinterConnection(load.SerialConfig.SerialPortName,
                                                   load.SerialConfig.SerialBaudRate);
            }
        }

        /// <summary>Human-readable description of the connection (for logging)</summary>
        private string DescribeConnection(PrinterConfigBLL.LoadResult load)
        {
            if (load.EnabledType == Model.Enums.ConnType.Tcp)
            {
                return "TCP " + load.TcpConfig.TcpIp + ":" + load.TcpConfig.TcpPort.ToString();
            }
            return "Serial " + load.SerialConfig.SerialPortName + " @" + load.SerialConfig.SerialBaudRate.ToString();
        }

        /// <summary>
        /// Spin up the resident threads (receive / send), all IsBackground=true (hard requirement).
        /// [2026-09-12] The heartbeat thread is disabled (keep the code, do not start it) — code sending fires once every 200ms,
        ///   and each round is itself a "write + wait for response" liveness probe; SEND_ACK_TIMEOUT_STREAK_MAX consecutive response timeouts judge the link
        ///   offline (see SendLoop), and the probing density (0.2-second level) is far higher than the original heartbeat (3-second interval).
        ///   The stop sequence's JoinThread(_heartbeatThread) is retained: it safely skips when the reference is null.
        /// </summary>
        private void StartThreads()
        {
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Name = "PrintService-Receive";
            _receiveThread.Start();

            // [2026-09-12] The heartbeat thread is disabled (code retained, not started). Original startup code:
            // _heartbeatThread = new Thread(HeartbeatLoop);
            // _heartbeatThread.IsBackground = true;
            // _heartbeatThread.Name = "PrintService-Heartbeat";
            // _heartbeatThread.Start();

            _sendThread = new Thread(SendLoop);
            _sendThread.IsBackground = true;
            _sendThread.Name = "PrintService-Send";
            _sendThread.Start();
        }

        /// <summary>
        /// Prefill cache: claim N not-printed codes in Id order and send them one by one (called by the startup flow).
        /// [2026-09-12] New semantics under timed code sending v3:
        /// [Acked] Sent normally → continue to the next one;
        /// [0x15/timeout/write failure] That code has already been placed into stash by SendOneCode (automatically resent every 200ms once
        ///   running), so the prefill stops here and no further new codes are sent, but the startup/reconnect flow continues — the old
        ///   "abort outright" behavior is no longer necessary (stash guarantees this code is not lost, and the disconnect case is handled
        ///   by the running-state consecutive-timeout detection taking over the reconnect);
        /// [ClaimFailed] Claim failed (database exception / code already marked) → still throw to abort (the anti-duplicate iron rule takes
        ///   priority; a claim failure means the code-claim path has a problem, and continuing to send later codes is pointless).
        /// </summary>
        /// <returns>Actual number sent (used for log display only; codes that went into stash are likewise marked printed, but are not included in the return value)</returns>
        private int PrefillCache()
        {
            List<CodeData> codes = CodeDataDAL.GetNotPrintedCodes(_targetCache);

            if (codes.Count == 0)
            {
                LogWarn("[Prefill] No not-printed codes in the database; the service enters the running state and waits for an import.");
                return 0;
            }

            int sent = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                SendResult result = SendOneCode(codes[i], false);

                if (result == SendResult.ClaimFailed)
                {
                    throw new InvalidOperationException("Code-claim failed while prefilling the cache (database exception or the code is already occupied); startup aborted.");
                }

                if (result != SendResult.Acked)
                {
                    // 0x15 / response timeout / write failure: that code has gone into stash and is automatically resent every 200ms in the running state, so the prefill wraps up early
                    LogWarn("[Prefill] Record " + (i + 1).ToString() + " was not confirmed by the inkjet printer (0x15/timeout/write failure); "
                            + "that code has been moved to stash for automatic resend, the remaining prefill is cancelled and the startup flow continues.");
                    break;
                }

                sent++;
            }

            return sent;
        }

        // ============================================================
        // 0x15 reject log rate limiting (2026-09-12: silent in the UI + a 30-second summary on disk)
        // ============================================================

        /// <summary>Interval of the 0x15 on-disk summary (milliseconds): at most one summary is written per 30-second window</summary>
        private const int NACK_SUMMARY_INTERVAL_MS = 30000;

        /// <summary>Time of the last 0x15 summary written to disk (TickCount64; 0 = never written, so the first one is logged immediately)</summary>
        private long _lastNackLogMs = 0;

        /// <summary>0x15 count accumulated since the last summary</summary>
        private int _nackCountSinceLog = 0;

        /// <summary>
        /// A 0x15 reject arrived (called by the receive thread): completely silent in the UI, and the on-disk log is rate-limited to a 30-second summary.
        /// [Background (2026-09-12)] When the cache is full the machine replies 0x15 to every extra send, and logging each one
        ///   would flood both the screen and the disk; so it accumulates a count and writes one summary to disk every 30 seconds (including the cumulative count).
        /// [Where the rejected codes go] Into _stashCode; the send thread resends every 200ms and they are automatically topped up into the cache once a slot
        ///   frees up — no code loss.
        /// [Caller] Only the receive thread (the NAK branch of ProcessRxBuffer), so the fields need no locking.
        /// </summary>
        private void OnNakReceived()
        {
            _nackCountSinceLog++;

            long now = Environment.TickCount64;

            if (now - _lastNackLogMs >= NACK_SUMMARY_INTERVAL_MS)
            {
                LogHelper.Instance.Warn("[Response] 0x15 inkjet printer reject (usually the internal cache is full; the rejected code has been left in stash for "
                        + "automatic resend), " + _nackCountSinceLog.ToString() + " rejects accumulated recently (one summary every 30 seconds, not shown in the UI)");
                _lastNackLogMs = now;
                _nackCountSinceLog = 0;
            }
        }

        /// <summary>
        /// Refresh the moment of the last successful "read data" (called by the receive thread when it reads n&gt;0).
        /// [Trust reads only] See the _lastIoMs field description; the write goes through Interlocked.Exchange to guarantee cross-thread visibility.
        /// [2026-09-12] The heartbeat thread is disabled, and this method is retained along with the heartbeat code (it takes effect automatically if HeartbeatLoop is started again).
        /// </summary>
        private void MarkIoActivity()
        {
            System.Threading.Interlocked.Exchange(ref _lastIoMs, Environment.TickCount64);
        }

        // ============================================================
        // Code sending (the core of timed code sending v3: code-claim occupancy model + stash resend + two-branch decision)
        // ============================================================

        /// <summary>
        /// Send one production code: for a new code, claim it first (mark printed) → single write → wait for the response → wrap up according to the
        /// 0x06/other two-branch decision.
        ///
        /// [Occupancy model (2026-09-10 17:29)] The first step for a new code is to mark it printed; only afterwards is it
        ///   written to the inkjet printer. The claim condition is always PrintStatus=0, so once this record is marked successfully no path can ever
        ///   claim it again — eradicating "duplicate claim" style duplicate printing at the root.
        /// [2026-09-12] [stash resend (2026-09-12, timed code sending v3)] isResend=true means this record is the code in stash that was "not
        ///   confirmed as accepted": it is not re-occupied (it was already marked printed long ago) and the send count is not incremented (resends are
        ///   not double counted); only "write + wait for response" is performed. Setting/clearing stash is wrapped up uniformly inside this method, so
        ///   callers need not worry about it:
        ///   · 0x06 confirms acceptance → stash is cleared (when isResend) → returns Acked, and a new code is claimed next round;
        ///   · 0x15 / response timeout / write failure → the new code is placed into stash (left untouched when isResend) → the same code is resent next
        /// round (without wasting codes).
        /// [Send count semantics (revised 2026-09-12)] Only a successful new-code claim increments it (a claim ≡ a decrease in not-printed
        ///   stock, preserving the dashboard's "base − send count" identity); stash resends are not counted.
        /// [Logging] 0x06 goes to the UI only (LogDevice); 0x15 is silent in the UI (the rate-limited summary lives in the receive thread's OnNakReceived);
        ///   write failures go to disk (LogError).
        /// </summary>
        /// <param name="code">The code to send</param>
        /// <param name="isResend">true = stash resend (no claim, no count); false = new code (claim + count)</param>
        /// <returns>Acked / Nacked / AckTimeout / ClaimFailed / WriteFailed (semantics in SendResult)</returns>
        private SendResult SendOneCode(CodeData code, bool isResend)
        {
            // ---------- 1. New code: claiming marks it (occupancy lock) + send count +1 (stash codes skip this step) ----------
            if (!isResend)
            {
                // Only codes that have never been written out can be marked (WHERE PrintStatus = 0 inside the DAL):
                //   · Marking succeeds (>0)          → this code is exclusively ours for this send, continue writing
                //   · Marking returns 0              → the code was already marked by another path (theoretically should not happen), do not send, anti-duplicate
                //   · Marking throws (database down) → anti-duplicate cannot be guaranteed, never send (better to miss a print than risk a duplicate)
                int marked;

                try
                {
                    marked = CodeDataDAL.MarkPrinted(code.Id, DateTime.Now.ToDbTimeString(), string.Empty);
                }
                catch (Exception ex)
                {
                    LogHelper.Instance.Error("Code-claim failed (Id=" + code.Id.ToString() + "), this record is not sent", ex);
                    LogError("[Send] " + code.CodeValue + " (Id=" + code.Id.ToString()
                             + ") code-claim failed; this record is not sent to prevent duplicate printing, please check the database!");
                    return SendResult.ClaimFailed;
                }

                if (marked <= 0)
                {
                    LogWarn("[Send] " + code.CodeValue + " (Id=" + code.Id.ToString()
                            + ") was already marked as printed (a suspected duplicate claim path); this record is not sent to prevent duplicate printing, please watch the log to trace the source!");
                    return SendResult.ClaimFailed;
                }

                // [2026-09-12] Send count +1: only a successful new-code claim counts (stash resends are not counted).
                //   Aligned with the identity "not-printed stock decrease ≡ send count" (the dashboard's "base − send count" semantics).
                System.Threading.Interlocked.Increment(ref _runSendCount);
            }

            // ---------- 2. Single write + wait for response (both new codes and stash codes go through only this step, under the same _ioLock) ----------
            byte[] frame = CodeNetProtocol.BuildCacheDataFrame(code.CodeValue);

            try
            {
                bool? ackResult;

                // [2026-09-10] After the lock-hold rework (22:33):
                //   the order is "hang the pending inbox first → write inside the lock → wait outside the lock → remove the inbox in finally",
                //   so the response can wake the waiter as soon as it arrives, and waiting does not hold _ioLock.
                PendingAck pending = BeginPendingAck();

                try
                {
                    lock (_ioLock)
                    {
                        EnsureConnection().Write(frame);
                    }

                    ackResult = WaitPendingAck(pending, SEND_ACK_TIMEOUT_MS);
                }
                finally
                {
                    EndPendingAck(pending);
                }

                FireDashboard();

                if (ackResult == true)
                {
                    // 0x06 = the machine confirms acceptance: the stash code's task is done (cleared), a new code is claimed next round
                    if (isResend)
                    {
                        _stashCode = null;
                    }

                    // [2026-09-10] Code send information goes to the UI only (not to disk) —
                    // the operator can see the "send one → accepted" cadence in the UI (0x06 display; 2026-09-12)
                    LogDevice("[Send] " + code.CodeValue + " (Id=" + code.Id.ToString()
                              + (isResend ? ") stash resend was accepted by the inkjet printer (0x06)"
                                          : ") was accepted by the inkjet printer (0x06), status → printed"));
                    return SendResult.Acked;
                }

                // Reaching here = 0x15 reject or response timeout: the code goes into stash and the same code is resent next round (without wasting codes)
                if (!isResend)
                {
                    _stashCode = code;
                }

                if (ackResult == false)
                {
                    // 0x15 = reject (usually the internal cache is full): completely silent in the UI (2026-09-12);
                    //   the on-disk summary is rate-limited to 30 seconds by the receive thread's OnNakReceived, so no duplicate log line here
                    return SendResult.Nacked;
                }

                // Response timeout = "written in but no feedback at all": the consecutive-timeout counter and the offline judgement are handled by SendLoop
                return SendResult.AckTimeout;
            }
            catch (Exception ex)
            {
                // Write failure (connection problem): the code stays printed and is not rolled back (iron rule of plan A), but is moved to stash for a resend next round
                // (2026-09-12: on a write failure the same code is resent too, without wasting codes);
                // the reconnect is triggered by SendLoop, and after the reconnect stash is preserved and automatically resent after the prefill
                if (!isResend)
                {
                    _stashCode = code;
                }

                LogHelper.Instance.Error("Code send write failed (Id=" + code.Id.ToString()
                                         + ", code=" + code.CodeValue + ")", ex);
                LogError("[Send] " + code.CodeValue + " write failed (" + ex.Message
                         + "): that code has been moved to stash for resend (without wasting codes), please watch the connection status!");

                FireDashboard();
                return SendResult.WriteFailed;
            }
        }

        /// <summary>
        /// Hang the "pending inbox" (**must be called before Write**).
        /// [2026-09-10] After the lock-hold rework (22:33): the order must be
        ///   "hang the inbox → then write → wait outside the lock → remove it in finally".
        /// [Why hanging the inbox first is mandatory] If we wrote first and hung the inbox afterwards, a response could arrive in between — at that moment
        ///   _pendingAck would still be null, so the receive thread (DeliverAck) would find no recipient and the response would be dropped; this transaction
        ///   would then hang its inbox but never receive a result, manifesting as "intermittent timeouts", which is harder to troubleshoot than the original
        ///   "fixed timeout".
        /// [Precondition] Only one transaction may be in flight at a time (this field is a single reference). This is currently guaranteed by the distribution of
        ///   call sites plus the _state / _printerOnline latches: the send thread, startup prefill, reconnect prefill and startup/reconnect instructions never
        ///   run concurrently.
        ///   If concurrent call sites are added later, this must change to "allocate a separate inbox per transaction".
        /// </summary>
        private PendingAck BeginPendingAck()
        {
            PendingAck pending = new PendingAck();

            lock (_ackLock)
            {
                _pendingAck = pending;
            }

            return pending;
        }

        /// <summary>
        /// Remove the "pending inbox" (must be called on success or failure alike, inside finally).
        /// [Edge case] Only remove the one we hung ourselves — this avoids mistakenly removing an inbox hung by a subsequent transaction.
        /// </summary>
        private void EndPendingAck(PendingAck pending)
        {
            lock (_ackLock)
            {
                if (object.ReferenceEquals(_pendingAck, pending))
                {
                    _pendingAck = null;
                }
            }
        }

        /// <summary>
        /// Wait for the receive thread to deliver the ACK **outside of _ioLock**.
        /// [2026-09-10] After the lock-hold rework (22:33):
        ///   The original implementation (WaitAckInsideIoLock) put the wait inside lock(_ioLock), but after reading the response bytes the receive
        ///   thread must first acquire that same _ioLock in order to deliver — the waiter holds the lock while the deliverer asks for it, so the
        ///   response could never get in and the full timeout was always waited out. Consequences: every production code send wasted 100ms; the 4
        ///   startup commands together wasted about 12 seconds.
        /// [Mechanism] First check whether Received is already set (a response may have arrived before this method enters the wait, for example the
        ///   machine replied 0x06 an extremely short time after the write); if set, take the result directly without entering Wait. Only sleep when it
        ///   is not set, and re-check in the loop after waking — this way even if Monitor.PulseAll happens before Monitor.Wait (the classic "lost
        ///   signal"), a timeout is not falsely declared.
        /// [Edge case] If the loop reaches the deadline without receiving anything → return null (timeout with no response), the same semantics as the old implementation.
        /// </summary>
        /// <returns>true = 0x06; false = 0x15; null = timeout with no response</returns>
        private bool? WaitPendingAck(PendingAck pending, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + (timeoutMs < 1 ? 1 : timeoutMs);

            lock (_ackLock)
            {
                while (!pending.Received)
                {
                    long remain = deadline - Environment.TickCount64;

                    if (remain <= 0)
                    {
                        break;
                    }

                    Monitor.Wait(_ackLock, (int)remain);
                }
            }

            if (!pending.Received)
            {
                return null;
            }

            return pending.Acked;
        }

        /// <summary>
        /// Send a protocol instruction and wait for the response (shared by print signal setup / queue clearing / test print).
        /// [v5 boundary] The response result only goes to the log — a NAK/timeout at the instruction layer does not change the state of any code.
        /// </summary>
        private void ExecInstruction(byte[] frame, string title)
        {
            try
            {
                bool? ackResult;

                // [2026-09-10] After the lock-hold rework (22:33): hang the inbox → write inside the lock → wait outside the lock → remove it in finally.
                //   The wait used to be inside lock(_ioLock), and reading the response on the receive thread required the same lock → each instruction wasted the
                //   full SendResponseTimeoutMs (3000ms by default), totaling about 12 seconds for the 4 startup instructions.
                PendingAck pending = BeginPendingAck();

                try
                {
                    lock (_ioLock)
                    {
                        EnsureConnection().Write(frame);
                    }

                    ackResult = WaitPendingAck(pending, ConfigHelper.SendResponseTimeoutMs);
                }
                finally
                {
                    EndPendingAck(pending);
                }

                if (ackResult == true)
                {
                    LogDevice("[Command] " + title + ": sent, the inkjet printer replied 0x06 (success)");
                }
                else if (ackResult == false)
                {
                    LogWarn("[Command] " + title + ": the inkjet printer replied 0x15 (rejected, please check the command/template)");
                }
                else
                {
                    LogWarn("[Command] " + title + ": response timeout (" + ConfigHelper.SendResponseTimeoutMs.ToString() + " milliseconds)");
                }
            }
            catch (Exception ex)
            {
                LogWarn("[Command] " + title + ": send exception — " + ex.Message);
            }
        }

        /// <summary>
        /// Send an instruction and wait for the response directly (**test print only**: the connection is a temporary standalone one and does not go
        /// through the resident receive thread).
        /// [Difference from ExecInstruction] The latter relies on the resident receive thread to deliver the ACK and writes the _connection field;
        ///   the test print does not start the resident threads, so it must read/write this temporary connection itself, hence a separate implementation.
        /// [Implementation] After writing, poll and read directly at RECEIVE_POLL_TIMEOUT_MS until 0x06 / 0x15 is recognized or the accumulated time exceeds the timeout.
        /// [Logging] Sends and responses go to the UI only (LogDevice; 19:53), making the interaction cycle easy for the operator to follow.
        /// </summary>
        /// <returns>true = 0x06; false = 0x15; null = timeout with no response</returns>
        private bool? WaitAckDirect(IPrinterConnection conn, byte[] frame, string title)
        {
            conn.Write(frame);

            int timeoutMs = ConfigHelper.SendResponseTimeoutMs;
            int waited = 0;
            byte[] buffer = new byte[256];

            while (waited < timeoutMs)
            {
                int n = conn.Read(buffer, RECEIVE_POLL_TIMEOUT_MS);
                waited += RECEIVE_POLL_TIMEOUT_MS;

                if (n <= 0)
                {
                    continue;
                }

                // In the test scenario we only need to recognize the first meaningful event byte (no full frame parsing required)
                for (int i = 0; i < n; i++)
                {
                    byte b = buffer[i];

                    if (b == CodeNetProtocol.ACK)
                    {
                        LogDevice("[Test print] " + title + ": sent, the inkjet printer replied 0x06 (success)");
                        return true;
                    }

                    if (b == CodeNetProtocol.NAK)
                    {
                        LogDevice("[Test print] " + title + ": the inkjet printer replied 0x15 (rejected)");
                        return false;
                    }
                }
            }

            LogDevice("[Test print] " + title + ": response timeout (" + timeoutMs.ToString() + " milliseconds)");
            return null;
        }

        /// <summary>Get the current connection; throws if unavailable (the caller treats it as a write exception)</summary>
        private IPrinterConnection EnsureConnection()
        {
            IPrinterConnection? conn = _connection;
            if (conn == null || !conn.IsConnected)
            {
                throw new InvalidOperationException("The inkjet printer connection is unavailable.");
            }
            return conn;
        }

        // ============================================================
        // Receive thread (the only entry point that reads the stream; routing guards against packet sticking)
        // ============================================================

        /// <summary>
        /// Receive loop: continuously read bytes → feed the buffer → split by frame characteristics.
        /// [Read timeout 15 milliseconds] For the rationale of the value see the RECEIVE_POLL_TIMEOUT_MS constant documentation:
        ///   it determines how often the thread wakes up when there is no data, affecting cancellation responsiveness and the idle spin frequency.
        /// [Lock contention eliminated] The connection layer's Socket.Poll wait segment has been moved out of the connection lock (2026-09-10 18:35),
        ///   so no lock is held during the wait and the reader no longer blocks the writer (code send / heartbeat); this method is only responsible for
        ///   feeding the bytes read into the buffer and routing them, and does not take part in lock contention.
        /// </summary>
        private void ReceiveLoop()
        {
            CancellationToken token = _cts == null ? CancellationToken.None : _cts.Token;
            byte[] readBuffer = new byte[1024];

            while (!token.IsCancellationRequested)
            {
                IPrinterConnection? conn = _connection;

                if (conn == null)
                {
                    // [2026-09-10] Hardcoded waits uniformly lowered to 50ms: the production line is fast, and the shorter the wait the more responsive it is
                    Thread.Sleep(50);
                    continue;
                }

                int n;
                try
                {
                    // [2026-09-10] Read timeout 200 → 50 → 15ms:
                    //   15ms determines "how often it wakes up when there is no data", making cancellation more responsive (a stop sequence joining the
                    //   receive thread waits about 15.6ms in the worst case). The connection layer's Poll is already outside the lock, so the writer
                    //   (code send / heartbeat) is no longer affected by this value. See the RECEIVE_POLL_TIMEOUT_MS constant documentation for the rationale.
                    n = conn.Read(readBuffer, RECEIVE_POLL_TIMEOUT_MS);
                }
                catch (ObjectDisposedException)
                {
                    // [2026-09-10] P1 race fix: the connection being Disposed does not only happen during the stop sequence —
                    //   SafeCloseConnection on a reconnect also closes the old connection, which may collide exactly with this thread
                    //   being blocked in a Read on the old connection. At that point the thread must never exit (if it exited, no one would
                    //   read the stream even after a successful reconnect, so ACK/status frames/0x32 would all be missed → false online → heartbeat
                    //   timeout → an infinite reconnect loop). Only the stop sequence (cancellation signal already set) may exit;
                    //   in the disconnect case, sleep and retry, and reading naturally resumes once the reconnect thread swaps in the new connection.
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }
                    Thread.Sleep(50);   // [2026-09-10] Hardcoded waits uniformly lowered to 50ms
                    continue;   // No data read this round (Read did not return): go straight to the next iteration, must not fall through to the n check
                }
                catch (Exception ex)
                {
                    // Read exception (serial cable unplugged / TCP reset, etc.): log it and retry shortly;
                    // a genuine disconnect is judged centrally by the heartbeat timeout, so no state switch here to avoid double triggering
                    LogWarn("[Receive] Read exception: " + ex.Message);
                    Thread.Sleep(50);   // [2026-09-10] Hardcoded waits uniformly lowered to 50ms
                    continue;
                }

                if (n <= 0)
                {
                    continue;
                }

                // [2026-09-10] Reading data refreshes the "last interaction time" (the heartbeat uses it to skip liveness probing).
                //   Only a successful read counts, not a successful write — see the _lastIoMs field description.
                MarkIoActivity();

                // [2026-09-12] Receiving any byte from the machine = the peer is alive: reset the "consecutive response timeout" counter
                //   (the heartbeat is disabled; disconnect detection is now handled by code-send response timeouts — see the _ackTimeoutStreak field description)
                System.Threading.Interlocked.Exchange(ref _ackTimeoutStreak, 0);

                lock (_rxLock)
                {
                    for (int i = 0; i < n; i++)
                    {
                        _rxBuffer.Add(readBuffer[i]);
                    }
                    ProcessRxBuffer();
                }
            }
        }

        /// <summary>
        /// Parse the receive buffer (must be called while holding _rxLock).
        /// [Routing rules: query commands must never be confused with print commands]
        ///   0x06 / 0x15    → ACK stream (delivered to the waiting transaction + logged)
        ///   0x32           → print-done event stream (cache count -1 + wake the send thread)
        /// 0x30           → a byte of unknown origin (2026-09-12: not a print-done signal; silently ignored for now, not shown in the UI, not written to disk)
        ///   1B 31 43…04    → status frame (heartbeat response)
        ///   1B 54 31…04    → counter frame (backup reconciliation, log only)
        ///   anything else  → written to the log verbatim in hexadecimal; never participates in business decisions
        /// </summary>
        private void ProcessRxBuffer()
        {
            while (_rxBuffer.Count > 0)
            {
                byte first = _rxBuffer[0];

                // ---------- Single-byte events ----------
                if (first == CodeNetProtocol.ACK)
                {
                    _rxBuffer.RemoveAt(0);
                    DeliverAck(true);
                    continue;
                }

                if (first == CodeNetProtocol.NAK)
                {
                    _rxBuffer.RemoveAt(0);
                    DeliverAck(false);
                    // [2026-09-12] 0x15 is completely silent in the UI, and the on-disk log is rate-limited to a 30-second summary (see OnNakReceived)
                    OnNakReceived();
                    continue;
                }

                if (first == CodeNetProtocol.PRINT_DONE)
                {
                    _rxBuffer.RemoveAt(0);
                    OnPrintDone();
                    continue;
                }

                // [2026-09-12] 0x30 is silently ignored on arrival (not shown in the UI, not written to disk).
                // [What 0x30 is — still undetermined] 0x30 is not a print-done signal (2026-09-12); its origin is
                //   unknown, so it is first silently discarded as an unknown byte. Only the questionable clues are noted here, with no conclusion drawn:
                //   SET_ACK (field B of the print signal setup frame 1B 49 31 32 04) can set a "print confirmation character"
                //   (legal values 1-4 / A-Z); this program sets '2' (0x32), and the simulator appears to reply '0' (0x30),
                //   which has not been confirmed on site and is for reference only.
                // [Behavior boundary] The print count only recognizes 0x32; 0x30 is simply discarded, and liveness probing is unaffected
                //   (ReceiveLoop already resets the consecutive-timeout counter upon receiving any byte).
                // [Frame safety] This branch only triggers when 0x30 sits at the head of the buffer; a 0x30 inside the content of a status frame/counter frame
                //   (such as SSS="000" or the HHMM time digits) is inside the 0x1B frame collection path and will not be mistakenly stripped.
                if (first == 0x30)
                {
                    _rxBuffer.RemoveAt(0);
                    continue;
                }

                // ---------- Complete frames ----------
                if (first == CodeNetProtocol.ESC)
                {
                    bool statusHead = _rxBuffer.Count >= 3 && _rxBuffer[1] == 0x31 && _rxBuffer[2] == 0x43;
                    bool countHead = _rxBuffer.Count >= 3 && _rxBuffer[1] == 0x54 && _rxBuffer[2] == 0x31;

                    if (statusHead)
                    {
                        if (_rxBuffer.Count < CodeNetProtocol.STATUS_FRAME_LENGTH)
                        {
                            break;  // Frame not fully received; wait for the next read cycle
                        }

                        int statusCode;
                        string frameText;
                        if (CodeNetProtocol.TryParseStatusFrame(_rxBuffer, out statusCode, out frameText)
                            && _rxBuffer[11] == CodeNetProtocol.EOT)
                        {
                            _rxBuffer.RemoveRange(0, CodeNetProtocol.STATUS_FRAME_LENGTH);
                            OnStatusFrame(statusCode, frameText);
                            continue;
                        }

                        // Header matches but the content is malformed: discard the whole segment as a garbage frame and log it verbatim
                        DumpUnknownFrame(CodeNetProtocol.STATUS_FRAME_LENGTH);
                        continue;
                    }

                    if (countHead)
                    {
                        if (_rxBuffer.Count < CodeNetProtocol.COUNT_FRAME_LENGTH)
                        {
                            break;
                        }

                        long count;
                        if (CodeNetProtocol.TryParseCountFrame(_rxBuffer, out count)
                            && _rxBuffer[13] == CodeNetProtocol.EOT)
                        {
                            _rxBuffer.RemoveRange(0, CodeNetProtocol.COUNT_FRAME_LENGTH);
                            LogDevice("[Counter frame] Print head has printed " + count.ToString() + " in total (backup reconciliation)");
                            continue;
                        }

                        DumpUnknownFrame(CodeNetProtocol.COUNT_FRAME_LENGTH);
                        continue;
                    }

                    // Starts with 0x1B but is not a recognized frame: discard the first byte and log it (the remaining bytes are judged next round,
                    // so even if the inkjet printer sends an unknown long frame the buffer will not get stuck)
                    _rxBuffer.RemoveAt(0);
                    LogInfo("[Receive] Unknown frame header byte 0x" + CodeNetProtocol.ToHexByte(first) + " (ignored)");
                    continue;
                }

                // ---------- Completely unrecognizable bytes ----------
                _rxBuffer.RemoveAt(0);
                LogInfo("[Receive] Unknown byte 0x" + CodeNetProtocol.ToHexByte(first) + " (ignored, not used in decisions)");
            }

            // Backstop against bloat: abnormal buffer growth means the peer is sending a garbage stream, so discard it all to prevent unbounded memory growth
            if (_rxBuffer.Count > 4096)
            {
                byte[] garbage = _rxBuffer.ToArray();
                _rxBuffer.Clear();
                LogWarn("[Receive] Buffer overflow (" + garbage.Length.ToString()
                        + " bytes), discarded entirely. Raw data: 0x" + CodeNetProtocol.ToHexString(garbage, 64));
            }
        }

        /// <summary>Discard the first count bytes of the buffer as a garbage frame and log them</summary>
        private void DumpUnknownFrame(int count)
        {
            if (count > _rxBuffer.Count)
            {
                count = _rxBuffer.Count;
            }

            byte[] garbage = new byte[count];
            for (int i = 0; i < count; i++)
            {
                garbage[i] = _rxBuffer[i];
            }
            _rxBuffer.RemoveRange(0, count);

            LogWarn("[Receive] Malformed frame discarded (" + count.ToString() + " bytes): 0x" + CodeNetProtocol.ToHexString(garbage, 64));
        }

        /// <summary>ACK delivery: wake the transaction waiting for the response (when there is no waiter, only log it)</summary>
        private void DeliverAck(bool acked)
        {
            lock (_ackLock)
            {
                if (_pendingAck != null)
                {
                    // [2026-09-10] Lock rework: set "received" first, then the result value — the waiter uses Received as its sole criterion, and because
                    //   both assignments happen inside the same _ackLock, it is guaranteed that when it sees Received=true, Acked is already the final value.
                    _pendingAck.Received = true;
                    _pendingAck.Acked = acked;
                }
                Monitor.PulseAll(_ackLock);
            }

            // The response itself also writes a run log line for easy reconciliation (a waiting transaction writes its own result separately)
            // [2026-09-10] UI only, not written to disk (it is "feedback for a code sent to the inkjet printer")
            if (acked)
            {
                LogDevice("[Response] 0x06 receive confirmation");
            }
        }

        /// <summary>
        /// Print-done event (0x32): dashboard "printed count" +1.
        /// [2026-09-10] The only accumulation point for the dashboard's "printed count" is here — only 0x32 counts.
        /// [2026-09-12] 0x32 is still received, displayed and counted as usual, but **no longer drives code sending**
        ///   (code sending has become a 200ms timed send, see SendLoop; the old quota AddPendingSend has been removed).
        /// </summary>
        private void OnPrintDone()
        {
            // Dashboard "printed count" +1 (accumulated for this run, reset to zero on Start; see the _runPrintedCount field description)
            System.Threading.Interlocked.Increment(ref _runPrintedCount);

            LogDevice("[Print done] The inkjet printer sent back 0x32; " + RunPrintedCount.ToString() + " printed so far in this run");

            // [2026-09-12] Code sending is now timer-driven, so there is currently no waiter; the signal is retained (directly usable if event-driven operation is restored)
            _printDoneEvent.Set();
        }

        /// <summary>Status frame (heartbeat response): lights the status frame signal + logs when the status code changes</summary>
        private void OnStatusFrame(int statusCode, string frameText)
        {
            _statusFrameEvent.Set();

            // SSS status code: 000 = ready/normal; 1xx warning; 2xx printing prohibited; 100 fault (PDF 4.5)
            if (statusCode != _lastLoggedStatusCode)
            {
                _lastLoggedStatusCode = statusCode;

                if (statusCode == 0)
                {
                    LogDevice("[Heartbeat] Inkjet printer status " + statusCode.ToString("D3") + " (ready/normal)");
                }
                else
                {
                    LogWarn("[Heartbeat] Inkjet printer status " + statusCode.ToString("D3")
                            + " (abnormal: 1xx warning / 2xx printing prohibited / 100 fault), frame: " + frameText);
                }
            }
        }

        // ============================================================
        // Heartbeat thread
        // ============================================================

        /// <summary>
        /// Heartbeat loop: periodically sends the "query machine status" command and waits for the status frame; a timeout judges the link offline and triggers a reconnect.
        /// [Online determination] A status frame received = online; no status frame within HeartbeatTimeoutMs = disconnected (by design).
        /// [Configuration takes effect immediately] The interval/timeout are re-read from ConfigHelper every round, so saving on the system parameters page takes effect next round.
        /// </summary>
        private void HeartbeatLoop()
        {
            CancellationToken token = _cts == null ? CancellationToken.None : _cts.Token;

            while (!token.IsCancellationRequested)
            {
                // ---------- Sliced sleep, for responsiveness to cancellation + immediate configuration changes ----------
                // [2026-09-10] Slice granularity uniformly lowered to 50ms: faster cancellation response, and heartbeat interval changes take effect sooner
                int interval = ConfigHelper.HeartbeatIntervalMs;
                int slept = 0;
                while (slept < interval && !token.IsCancellationRequested)
                {
                    Thread.Sleep(50);
                    slept += 50;

                    // [2026-09-10] Watch idleness while sleeping — as soon as a full heartbeat interval has passed
                    //   since "the last successful read", end this round of sleep immediately to probe for liveness, rather than going back to sleep a whole round.
                    //   Reason: the previous "sleep the whole round before judging idleness" had a phase penalty — if the last data record happened to be read
                    //         at the very end of the sleep window (idle just reset to zero), the on-time judgement would necessarily be "less than the interval"
                    //         and skip, wasting a whole round, and disconnect detection could be stretched to 2×interval + timeout (about 8 seconds) in the worst
                    //         case. This brings the worst case back to the design floor of "interval + timeout" (about 5 seconds).
                    //   During continuous production reads, idle is always < interval, so it still sleeps the whole round and skips probing; the locking benefit is unchanged.
                    long lastIoNow = System.Threading.Interlocked.CompareExchange(ref _lastIoMs, 0, 0);
                    if (Environment.TickCount64 - lastIoNow >= interval)
                    {
                        break;
                    }
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (!IsRunning || !_printerOnline)
                {
                    continue;   // During a disconnect the reconnect thread is in charge; the heartbeat does nothing
                }

                // [2026-09-10] Skip when idle — when data was successfully read recently (meaning the connection is
                //   certainly alive and there was just an interaction) no query frame is sent this round; only "at least a heartbeat interval since the last
                //   successful read" triggers real probing.
                // During continuous production reads always produce data → the heartbeat essentially never runs → fewer read/write lock conflicts (by design).
                // 
                //   Half-dead device scenario (TCP hanging but not responding): no data can be read → _lastIoMs stops updating → after the idle timeout the
                //   heartbeat steps in → no status frame received → judged disconnected (this is exactly the key value of "trust reads, not writes").
                long lastIo = System.Threading.Interlocked.CompareExchange(ref _lastIoMs, 0, 0);
                long idleMs = Environment.TickCount64 - lastIo;

                if (idleMs < interval)
                {
                    continue;
                }

                try
                {
                    // Reset the signal → write the query frame → wait for the status frame
                    _statusFrameEvent.Reset();

                    // [2026-09-10] The normal heartbeat state is also shown in the UI (so you can see the system is running)
                    LogDevice("[Heartbeat] Sending the query status command (idle liveness probe).");

                    lock (_ioLock)
                    {
                        EnsureConnection().Write(CodeNetProtocol.BuildQueryStatusFrame());
                    }

                    bool got = _statusFrameEvent.WaitOne(ConfigHelper.HeartbeatTimeoutMs < 1 ? 1 : ConfigHelper.HeartbeatTimeoutMs);

                    if (!got)
                    {
                        // [2026-09-10] B2 fix: after a timeout we must first confirm the service is still running —
                        //   after the stop sequence Cancels, this thread may happen to wake from WaitOne (a race exists between the wait window and Cancel),
                        //   and without this check it would repaint the already-stopped service as a red "Offline" and spin up an idle reconnect thread.
                        if (token.IsCancellationRequested || !IsRunning)
                        {
                            break;
                        }

                        LogWarn("[Heartbeat] No status frame within " + ConfigHelper.HeartbeatTimeoutMs.ToString()
                                + " milliseconds; the inkjet printer is judged disconnected.");
                        TriggerReconnect();
                    }
                }
                catch (Exception ex)
                {
                    // Same as above: a write exception occurring during the stop sequence must not trigger another reconnect
                    if (token.IsCancellationRequested || !IsRunning)
                    {
                        break;
                    }

                    // A failed heartbeat write = the connection has a problem, switch to reconnect
                    LogWarn("[Heartbeat] Send exception: " + ex.Message);
                    TriggerReconnect();
                }
            }
        }

        // ============================================================
        // Send thread (timed code sending v3: one send every 200ms, decoupled from 0x32)
        // ============================================================

        /// <summary>
        /// Send loop: sends one record every SEND_INTERVAL_MS (200ms) — if stash is non-empty, resend the stash code; if empty, claim a new code
        /// (claiming occupies it). Two-branch response decision:
        ///   0x06 = the machine confirms acceptance → claim a new code next round (stash is cleared inside SendOneCode);
        ///   0x15 / response timeout / write failure → the code goes into stash (done inside SendOneCode), and the same code is resent next round.
        /// [2026-09-12] The timing of code sending is completely decoupled from 0x32 — 0x32 is used only for UI display and the
        /// "printed count".
        /// [Disconnect detection (the heartbeat is disabled)] SEND_ACK_TIMEOUT_STREAK_MAX consecutive response timeouts (3 by default)
        ///   → judged disconnected → TriggerReconnect; the receive thread resets the counter upon receiving any byte from the machine (see ReceiveLoop).
        ///   With the default parameters (timeout 2000ms + cadence 200ms) a disconnect is detected in about 3 × 2.2 ≈ 6.6 seconds in the worst case.
        /// [Write failure] The code has gone into stash and a reconnect is triggered immediately (a connection-layer problem); after a successful reconnect
        /// stash is preserved and queues behind the 3 prefilled records, waiting for a slot to be resent automatically (a few beats
        ///   later loses nothing).
        /// [No-code behavior] When stash is empty and the database has no not-printed codes: skip this round and wait for an import, reporting it only once
        ///   to avoid flooding the screen.
        /// [2026-09-10] P3-2: mutually exclusive with the startup/reconnect prefill — during prefill IsRunning=false and _printerOnline=false, so this thread
        ///   is blocked right at the loop entrance below and will not concurrently claim the same not-printed code.
        ///   If the order of the prefill/latches is ever changed, the two conditions here must be re-verified.
        /// </summary>
        private void SendLoop()
        {
            CancellationToken token = _cts == null ? CancellationToken.None : _cts.Token;

            while (!token.IsCancellationRequested)
            {
                // [2026-09-12] Timed cadence: one send per SEND_INTERVAL_MS (kept as a static variable, see the constant definition)
                Thread.Sleep(SEND_INTERVAL_MS);

                if (token.IsCancellationRequested || !IsRunning || !_printerOnline)
                {
                    continue;
                }

                // ---------- 1. Determine the code to send this round: stash takes priority; only query the database for a new code when there is no stash ----------
                CodeData? codeToSend = null;
                bool isResend = false;

                if (_stashCode != null)
                {
                    // stash is non-empty: resend the code that was not confirmed as accepted last time (no re-claiming, no re-occupying, no double counting)
                    codeToSend = _stashCode;
                    isResend = true;
                }
                else
                {
                    List<CodeData> codes = CodeDataDAL.GetNotPrintedCodes(1);

                    if (codes.Count == 0)
                    {
                        // No codes in the database: skip this round and wait for an import; report only once to avoid flooding the screen
                        if (!_noCodeLogged)
                        {
                            _noCodeLogged = true;
                            LogWarn("[Send] No not-printed codes in the database; retrying automatically every " + SEND_INTERVAL_MS.ToString()
                                    + " milliseconds, waiting for an import.");
                        }
                        continue;
                    }

                    _noCodeLogged = false;
                    codeToSend = codes[0];
                }

                // ---------- 2. Send (the claim/count/stash wrap-up all happen inside SendOneCode) ----------
                SendResult result = SendOneCode(codeToSend, isResend);

                // ---------- 3. Result routing: only the three categories that require a SendLoop decision ----------
                if (result == SendResult.AckTimeout)
                {
                    // Consecutive response timeout counter (the receive thread resets it upon receiving any byte);
                    // reaching the threshold = "written in but consistently no feedback" → judged disconnected (the only disconnect source after the heartbeat was disabled)
                    int streak = System.Threading.Interlocked.Increment(ref _ackTimeoutStreak);

                    if (streak >= SEND_ACK_TIMEOUT_STREAK_MAX)
                    {
                        LogWarn("[Send] " + streak.ToString() + " consecutive code-send response timeouts; the inkjet printer is judged disconnected.");
                        System.Threading.Interlocked.Exchange(ref _ackTimeoutStreak, 0);
                        TriggerReconnect();
                    }
                }
                else if (result == SendResult.WriteFailed)
                {
                    // Write failure: the connection has a problem, switch to reconnect immediately (the code is already in stash and will be resent after reconnecting)
                    LogWarn("[Send] Write failed, switching to the reconnect flow.");
                    TriggerReconnect();
                }
                else if (result == SendResult.ClaimFailed)
                {
                    // Claim failed (database exception / code occupied): the code was not occupied and is still in the database, so the next round's re-claim retries it naturally;
                    // the connection is fine, so no reconnect
                    LogWarn("[Send] Code-claim failed, skipping this round (no reconnect, retry next round).");
                }
                // Acked / Nacked: no extra handling needed — clearing/keeping stash was already wrapped up inside SendOneCode;
                // the 0x15 log is rate-limited into a summary by the receive thread's OnNakReceived
            }
        }

        // ============================================================
        // Reconnect
        // ============================================================

        /// <summary>
        /// Trigger a reconnect (called on heartbeat timeout / code-send write failure).
        /// [Mutual exclusion] _reconnectFlag prevents several places from spinning up the reconnect thread at once.
        /// </summary>
        private void TriggerReconnect()
        {
            // [2026-09-10] B2 fix: gate at the entrance — when the service is stopped / stopping / the cancellation signal is already set,
            //   the reconnect thread must never be spun up (a leftover heartbeat thread at the moment of stopping may land exactly here).
            if (!IsRunning)
            {
                return;
            }

            CancellationTokenSource? cts = _cts;
            if (cts == null || cts.IsCancellationRequested)
            {
                return;
            }

            if (System.Threading.Interlocked.CompareExchange(ref _reconnectFlag, 1, 0) != 0)
            {
                return; // A reconnect is already running
            }

            _printerOnline = false;
            FirePrinterState("Offline", System.Drawing.Color.Red);

            try
            {
                SafeCloseConnection("Disconnect handling");
            }
            catch
            {
                // A close failure does not affect the reconnect flow
            }

            _reconnectThread = new Thread(ReconnectLoop);
            _reconnectThread.IsBackground = true;
            _reconnectThread.Name = "PrintService-Reconnect";
            _reconnectThread.Start();
        }

        /// <summary>
        /// Reconnect loop: retries repeatedly at the configured interval; on success it re-initializes the protocol environment and tops up the cache.
        /// [Rationale for re-computation] After a disconnect the machine's internal queue content is no longer trustworthy (it may have printed part of it /
        ///   been cleared by a reset), so the cache count is reset to zero; codes in the cache during the disconnect stay printed per the v5 write model and
        ///   are never resent.
        /// </summary>
        private void ReconnectLoop()
        {
            CancellationToken token = _cts == null ? CancellationToken.None : _cts.Token;

            try
            {
                while (!token.IsCancellationRequested && IsRunning)
                {
                    int interval = ConfigHelper.ReconnectIntervalMs;
                    LogInfo("[Reconnect] Attempting to reconnect in " + interval.ToString() + " milliseconds...");

                    // Sliced sleep
                    // [2026-09-10] Slice granularity uniformly lowered to 50ms (consistent with the heartbeat), for faster cancellation response
                    int slept = 0;
                    while (slept < interval && !token.IsCancellationRequested && IsRunning)
                    {
                        Thread.Sleep(50);
                        slept += 50;
                    }

                    if (token.IsCancellationRequested || !IsRunning)
                    {
                        break;
                    }

                    try
                    {
                        // 1. Create the connection from the latest configuration (it may have been changed on site in the meantime)
                        PrinterConfigBLL.LoadResult load = PrinterConfigBLL.Load();
                        if (!load.Success)
                        {
                            throw new InvalidOperationException(load.Message);
                        }

                        IPrinterConnection conn = CreateConnection(load);
                        conn.Open();
                        _connection = conn;

                        // 2. Re-initialize the protocol environment (the connection is new, so the signal setup must be resent)
                        ExecInstruction(CodeNetProtocol.BuildSignalSetupFrame(), "Print signal setup after reconnect");
                        ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(0), "Clear TCP/IP queue after reconnect");
                        ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(1), "Clear RS232 queue after reconnect");
                        ExecInstruction(CodeNetProtocol.BuildClearQueueFrame(2), "Clear history queue after reconnect");

                        // 3. Reset the consecutive timeout counter + discard the old receive buffer
                        // [2026-09-12] Timed code sending v3: the old quota ResetPendingSend has been removed;
                        // stash is **preserved** — after reconnecting the stash code queues behind the 3 prefilled records
                        // waiting for a slot, and the send thread resends it every 200ms, so it is a few beats later but never lost.
                        System.Threading.Interlocked.Exchange(ref _ackTimeoutStreak, 0);
                        lock (_rxLock)
                        {
                            _rxBuffer.Clear();
                        }

                        // 4. Top up the cache (occupancy model: claiming each code marks it printed)
                        // [2026-09-12] Timed code sending v3: 0x15/timeout/write failure → that code has gone into stash, so the prefill wraps up early
                        //   (the running state resends every 200ms automatically) and the reconnect continues; a claim failure still aborts the reconnect (anti-duplicate takes priority)
                        List<CodeData> codes = CodeDataDAL.GetNotPrintedCodes(_targetCache);
                        int prefilled = 0;
                        for (int i = 0; i < codes.Count; i++)
                        {
                            SendResult result = SendOneCode(codes[i], false);

                            if (result == SendResult.ClaimFailed)
                            {
                                throw new InvalidOperationException("Prefilling the cache after reconnect failed (code-claim failed).");
                            }

                            if (result != SendResult.Acked)
                            {
                                LogWarn("[Reconnect] Prefill record " + (i + 1).ToString()
                                        + " was not confirmed by the inkjet printer (0x15/timeout/write failure); that code has been moved to stash for automatic resend and the remaining prefill is cancelled.");
                                break;
                            }

                            prefilled++;
                        }

                        // 5. Back online
                        // [2026-09-10] Defensive (15:11): during the prefill the user may have already clicked "Stop Printing"
                        //   — after the stop sequence's Join on the reconnect thread times out it completes the seven steps first (the connection is cleaned
                        //   up by this thread as a backstop), so at this point the state/UI must never be pulled back to "Running"; close the freshly created
                        //   connection and exit directly.
                        if (token.IsCancellationRequested || !IsRunning)
                        {
                            LogInfo("[Reconnect] The service had already stopped when the reconnect completed; closing this connection and exiting.");
                            SafeCloseConnection("Cleanup after reconnect completed during stop");
                            break;
                        }

                        _printerOnline = true;
                        FirePrinterState("Running", System.Drawing.Color.Green);
                        LogInfo("[Reconnect] Reconnect succeeded, prefilled " + prefilled.ToString()
                                + " records, resuming timed code sending every " + SEND_INTERVAL_MS.ToString()
                                + " milliseconds (stash preserved for resend).");
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogWarn("[Reconnect] Failed: " + ex.Message);
                        SafeCloseConnection("Cleanup after reconnect failure");
                    }
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _reconnectFlag, 0);
            }
        }

        // ============================================================
        // Test print
        // ============================================================

        /// <summary>
        /// Test print sequence (executed on a separate thread): create a separate connection → print signal setup → clear the three queues →
        ///   send the fixed value AB12345 → close and release the connection.
        /// [Fully independent] It does not reuse the production connection, does not consume a production code Id, does not write to the database and does
        ///   not take part in the code-refill quota; production must already be stopped during a test (guaranteed by mutual exclusion), so clearing the three
        ///   queues does not affect any in-flight production data.
        /// [Release iron rule] Regardless of success / reject / timeout / exception,
        ///   finally always calls Close() + Dispose() and nulls the reference, never leaving an unclosed connection object behind.
        /// </summary>
        private void TestPrintSequence()
        {
            // The connection object is declared outside the try and initialized to null, so that even if Open throws it can be null-checked and released in finally
            IPrinterConnection? testConn = null;

            try
            {
                // ---------- 1. Read the configuration and create a separate connection ----------
                PrinterConfigBLL.LoadResult load = PrinterConfigBLL.Load();
                if (!load.Success)
                {
                    LogDevice("[Test print] Failed to read the inkjet printer configuration: " + load.Message);
                    return;
                }

                testConn = CreateConnection(load);
                _testConnection = testConn;      // For the stop sequence / program exit to close as a backstop
                testConn.Open();
                LogDevice("[Test print] Separate connection established (" + DescribeConnection(load) + ").");

                // ---------- 2. Print signal setup + clear the three cache queues (the same command group as production startup) ----------
                WaitAckDirect(testConn, CodeNetProtocol.BuildSignalSetupFrame(), "Print signal setup");
                WaitAckDirect(testConn, CodeNetProtocol.BuildClearQueueFrame(0), "Clear TCP/IP cache queue");
                WaitAckDirect(testConn, CodeNetProtocol.BuildClearQueueFrame(1), "Clear RS232 cache queue");
                WaitAckDirect(testConn, CodeNetProtocol.BuildClearQueueFrame(2), "Clear history cache queue");

                // ---------- 3. Send the fixed value AB12345 and wait for the 0x06 response ----------
                bool? ack = WaitAckDirect(testConn, CodeNetProtocol.BuildCacheDataFrame("AB12345"), "Test code AB12345");

                if (ack == true)
                {
                    LogDevice("[Test print] AB12345 was accepted by the inkjet printer (0x06); please observe the print head output.");
                }
                else if (ack == false)
                {
                    LogDevice("[Test print] AB12345 was rejected by the inkjet printer (0x15): please check whether the dynamic text template has been set on the machine!");
                }
                else
                {
                    LogDevice("[Test print] Response timeout after sending AB12345; please observe the print result.");
                }
            }
            catch (Exception ex)
            {
                // Connect failure / write failure, etc.: UI only, so the operator can see it on the spot (not written to disk)
                LogDevice("[Test print] Execution failed: " + ex.Message);
            }
            finally
            {
                // ---------- 4. Whether it succeeds or fails, fully release this test connection (iron rule) ----------
                try
                {
                    IPrinterConnection? conn = testConn;
                    testConn = null;

                    if (conn != null)
                    {
                        conn.Close();       // Interface contract: safe to call repeatedly, never throws
                        conn.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    // A release exception must never propagate out (this block is already inside finally;
                    // throwing here would mask the earlier execution result)
                    LogHelper.Instance.Error("Test print: failed to close the connection", ex);
                }
                _testConnection = null;

                // ---------- 5. State reset (reset only while still "Testing", to avoid fighting with the stop sequence) ----------
                bool needReset;
                lock (_stateLock)
                {
                    needReset = (_state == ServiceState.Testing);
                    if (needReset)
                    {
                        _state = ServiceState.Stopped;
                    }
                }

                System.Threading.Interlocked.Exchange(ref _testFlag, 0);
                _testPrintThread = null;

                if (needReset)
                {
                    FireServiceState("Not Started", false);
                }
            }
        }

        // ============================================================
        // Stop sequence (seven fixed steps in order; any failing step still continues to the end — no half-measures)
        // ============================================================

        /// <summary>Stop-sequence re-entry flag: 0=idle 1=running (prevents the Stop background thread and StopSync from executing the sequence twice concurrently)</summary>
        private int _stopFlag = 0;

        /// <summary>
        /// Stop-sequence entry point (with re-entry protection).
        /// [2026-09-10] Defence (?): when the user clicks "End Coding" and immediately closes the form,
        ///   the background thread spawned by Stop and the StopSync triggered by FormClosed may enter the
        ///   stop sequence at the same time —— repeating the seven steps is mostly harmless (Cancel/Join/
        ///   close-connection all null-check), but CTS double Dispose and double log writes are still dirty
        ///   concurrency. An Interlocked flag is used here to reject re-entry: the later caller returns
        ///   immediately, and the earlier caller is responsible for running the whole sequence and
        ///   restoring the UI.
        /// </summary>
        private void StopSteps(string reason)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _stopFlag, 1, 0) != 0)
            {
                LogInfo("[Stop] The stop sequence is already running (this call came from: " + reason + "), ignoring the duplicate call.");
                return;
            }

            try
            {
                StopStepsCore(reason);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _stopFlag, 0);
            }
        }

        /// <summary>
        /// The actual body of the seven-step stop sequence (requirement: all threads must exit, all objects
        /// fully released):
        ///   ① stop heartbeat (cancel signal) → ② stop receive/heartbeat/send/reconnect threads (Join) →
        ///   ③ clear the inkjet printer queue → ④ do not roll back any code (iron rule of the
        ///   code-claim occupancy model) → ⑤ close the connection → ⑥ release objects → ⑦ restore the UI.
        /// </summary>
        private void StopStepsCore(string reason)
        {
            LogInfo("[Stop] Starting the stop sequence (" + reason + ")");

            // ---------- ① Stop heartbeat (stop first, so no heartbeat is sent during the stop) ----------
            try
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Stop sequence: failed to cancel the token", ex);
            }
            _printerOnline = false;

            // ---------- ② Stop the receive/heartbeat/send/reconnect threads ----------
            // [2026-09-10] B2 fix: the heartbeat thread must be included —— missing its Join lets it wake up
            //   only after the stop sequence has finished, painting lblPrinterStatus red "Offline" (it has
            //   actually stopped, so it should show "Disconnected"), and it may even spawn an idle
            //   reconnect thread.
            JoinThread(_receiveThread, "receive thread");
            JoinThread(_heartbeatThread, "heartbeat thread");
            JoinThread(_sendThread, "send thread");
            JoinThread(_reconnectThread, "reconnect thread");
            JoinThread(_testPrintThread, "test print thread");
            _receiveThread = null;
            _heartbeatThread = null;
            _sendThread = null;
            _reconnectThread = null;
            _testPrintThread = null;

            // ---------- ③ Clear the inkjet printer queue (only sent while the connection is still alive; a failure does not affect later steps) ----------
            //   Clear the leftover unprinted data inside the machine so the inkjet printer returns to a clean state;
            //   the codes corresponding to that leftover data have already been marked as printed under the v5
            //   write model, so they are not resent.
            try
            {
                IPrinterConnection? conn = _connection;
                if (conn != null && conn.IsConnected)
                {
                    lock (_ioLock)
                    {
                        conn.Write(CodeNetProtocol.BuildClearQueueFrame(0));
                        conn.Write(CodeNetProtocol.BuildClearQueueFrame(1));
                        conn.Write(CodeNetProtocol.BuildClearQueueFrame(2));
                    }
                    LogInfo("[Stop] Sent the clear commands for all three cache queues (leftover data inside the machine has been cleared).");
                }
            }
            catch (Exception ex)
            {
                LogWarn("[Stop] Failed to clear the queue (the connection may already be down; continuing with later steps): " + ex.Message);
            }

            // ---------- ④ v5 write model: do not roll back any code ----------
            //   Codes already marked as printed stay printed (even if the machine has not physically printed
            //   them yet); never roll back and never resend, to prevent duplicate printing (iron rule).

            // ---------- ⑤ Close the connection (including the test print's separate connection) ----------
            SafeCloseConnection(reason);
            CloseTestConnection(reason);

            // ---------- ⑥ Release objects ----------
            try
            {
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Stop sequence: failed to release the cancellation token", ex);
            }

            lock (_ackLock)
            {
                _pendingAck = null;
            }
            // [2026-09-12] Timed code sending v3: clearing the stash on stop (decision: burning 1 code on
            //   shutdown is acceptable); the original quota ResetPendingSend has been removed.
            //   [Timing] At this point the send thread has already been Joined in ② (or abandoned on
            //   timeout), so the stash will not be written back again.
            _stashCode = null;
            lock (_rxLock)
            {
                _rxBuffer.Clear();
            }
            System.Threading.Interlocked.Exchange(ref _reconnectFlag, 0);
            System.Threading.Interlocked.Exchange(ref _testFlag, 0);

            // ---------- ⑦ Restore the UI ----------
            lock (_stateLock)
            {
                _state = ServiceState.Stopped;
            }
            FirePrinterState("Disconnected", System.Drawing.Color.FromArgb(0, 64, 0));
            FireServiceState("Not Started", false);
            LogInfo("[Stop] Stop sequence complete; the service is back to the Not Started state.");
        }

        /// <summary>Wait for a thread to exit (at most 3 seconds); on timeout log it and give up —— the threads are background threads and will not block process exit</summary>
        private void JoinThread(Thread? thread, string name)
        {
            try
            {
                if (thread != null && thread.IsAlive)
                {
                    if (!thread.Join(3000))
                    {
                        LogWarn("[Stop] " + name + " did not exit within 3 seconds; giving up the wait (a background thread does not block process exit).");
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Stop sequence: failed to wait for " + name + " to exit", ex);
            }
        }

        /// <summary>Safely close and release the connection (all exceptions are swallowed; the caller's flow is never affected)</summary>
        private void SafeCloseConnection(string reason)
        {
            try
            {
                IPrinterConnection? conn = _connection;
                _connection = null;

                if (conn != null)
                {
                    conn.Close();
                    conn.Dispose();
                    LogInfo("[Connection] Closed (" + reason + ").");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to close the inkjet printer connection (ignored, continuing)", ex);
            }
        }

        /// <summary>
        /// Safely close and release the test print's separate connection (all exceptions are swallowed; the
        /// caller's flow is never affected).
        /// [Scenario] Application exit / stop-sequence fallback —— if the test thread has already released it
        /// itself, this is a no-op, and it is safe to call repeatedly.
        /// </summary>
        private void CloseTestConnection(string reason)
        {
            try
            {
                IPrinterConnection? conn = _testConnection;
                _testConnection = null;

                if (conn != null)
                {
                    conn.Close();
                    conn.Dispose();
                    LogHelper.Instance.Info("[Test print] The separate connection has been closed (" + reason + ").");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to close the test print connection (ignored, continuing)", ex);
            }
        }

        // ============================================================
        // Logging and events (LogHelper writes to disk + events feed the UI)
        // ============================================================

        /// <summary>Normal log: write to disk + push to the UI run-log area</summary>
        private void LogInfo(string message)
        {
            LogHelper.Instance.Info(message);
            FireRunLog(message);
        }

        /// <summary>Warning log: write to disk (error duplicate not written, warn level) + push to the UI</summary>
        private void LogWarn(string message)
        {
            LogHelper.Instance.Warn(message);
            FireRunLog(message);
        }

        /// <summary>Error log: write to disk (also into the error duplicate) + push to the UI</summary>
        private void LogError(string message)
        {
            LogHelper.Instance.Error(message);
            FireRunLog(message);
        }

        /// <summary>
        /// Device interaction log: **pushed to the UI only, never written to disk** (directive of 2026-09-10
        /// 19:53).
        /// [Purpose] Let the operator follow the "send code → 0x06 received → 0x32 print done" interaction
        ///   rhythm in the run-log area: it covers every line of the 0x06 response / 0x32 print done /
        ///   [Send code] / command success / heartbeat send-receive / counter frames / test print.
        /// [Why not written to disk] On a production line doing 3 bottles per second, that is 3 interaction
        ///   lines per bottle → several hundred thousand lines a day; the disk log would be flooded and the
        ///   genuine read/write exceptions we need to trace would become unfindable. Only business/state-machine
        ///   events + read/write exceptions are written to disk.
        /// [Boundary] When the message is empty, FireRunLog would push a blank line; skipped here to avoid
        ///   meaningless blank lines in the UI.
        /// </summary>
        private void LogDevice(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            FireRunLog(message);
        }

        private void FireRunLog(string message)
        {
            Action<string>? handler = RunLog;
            if (handler != null)
            {
                try
                {
                    handler(message);
                }
                catch (Exception ex)
                {
                    // An exception on the UI side must never back-propagate into the background thread
                    System.Diagnostics.Debug.WriteLine("RunLog event handler exception: " + ex.Message);
                }
            }
        }

        private void FireServiceState(string text, bool isRunning)
        {
            Action<string, bool>? handler = ServiceStateChanged;
            if (handler != null)
            {
                try
                {
                    handler(text, isRunning);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ServiceStateChanged event handler exception: " + ex.Message);
                }
            }
        }

        private void FirePrinterState(string text, System.Drawing.Color color)
        {
            Action<string, System.Drawing.Color>? handler = PrinterStateChanged;
            if (handler != null)
            {
                try
                {
                    handler(text, color);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("PrinterStateChanged event handler exception: " + ex.Message);
                }
            }
        }

        private void FireDashboard()
        {
            Action? handler = DashboardChanged;
            if (handler != null)
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("DashboardChanged event handler exception: " + ex.Message);
                }
            }
        }
    }
}


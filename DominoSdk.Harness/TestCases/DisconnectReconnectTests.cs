using DominoA200Sdk;
using DominoA200Sdk.Core;
using DominoA200Sdk.Exceptions;
using DominoA200Sdk.Models;
using DominoSdk.Harness.TestFixture;
using Xunit;

namespace DominoSdk.Harness.TestCases;

/// <summary>
/// Verifies disconnect handling and the automatic reconnect path.
///
/// <para>
/// Reconnect is the hardest behaviour to get right in a printer integration: the link
/// can drop mid-shift and the software has to restore itself without operator
/// intervention. This test forces the drop by tearing the simulator down and bringing
/// it back on the same port.
/// </para>
/// </summary>
[Collection("MockServer")]
public sealed class DisconnectReconnectTests
{
    private readonly MockServerFixture _fixture;

    /// <summary>Receives the shared simulator fixture.</summary>
    public DisconnectReconnectTests(MockServerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// An explicit disconnect should raise the disconnect event once and leave the
    /// client in the disconnected state.
    /// </summary>
    [Fact]
    public async Task DisconnectAsync_RaisesEventAndUpdatesState()
    {
        using DominoA200Client client = new DominoA200Client(_fixture.Host, _fixture.Port);

        int disconnectedEvents = 0;
        client.OnDisconnected += (sender, args) => Interlocked.Increment(ref disconnectedEvents);

        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        await client.DisconnectAsync();

        Assert.False(client.IsConnected);
        Assert.Equal(PrinterState.Disconnected, client.State);
        Assert.Equal(1, Volatile.Read(ref disconnectedEvents));
    }

    /// <summary>
    /// Calling disconnect twice must be safe and must not raise a second event.
    /// </summary>
    [Fact]
    public async Task DisconnectAsync_IsIdempotent()
    {
        using DominoA200Client client = new DominoA200Client(_fixture.Host, _fixture.Port);

        int disconnectedEvents = 0;
        client.OnDisconnected += (sender, args) => Interlocked.Increment(ref disconnectedEvents);

        await client.ConnectAsync();

        await client.DisconnectAsync();
        await client.DisconnectAsync();

        Assert.Equal(1, Volatile.Read(ref disconnectedEvents));
    }

    /// <summary>
    /// After a disconnect the client must be usable again on a fresh connection, and
    /// its FIFO mirror must have been reset.
    /// </summary>
    [Fact]
    public async Task Reconnect_AfterDisconnectRestoresUsableSession()
    {
        using DominoA200Client client = new DominoA200Client(_fixture.Host, _fixture.Port);

        await client.ConnectAsync();
        await client.SendPrintJobAsync(new PrintJob("2026-09-15", "RECON00001"));

        await client.DisconnectAsync();
        Assert.False(client.IsConnected);

        // Reconnecting must clear stale mirror state and complete the handshake again.
        await client.ConnectAsync();

        Assert.True(client.IsConnected);
        Assert.Equal(PrinterState.Idle, client.State);
        Assert.Equal(0, await client.GetFifoQueueCountAsync());

        // And the session must be fully functional.
        string jobId = await client.SendPrintJobAsync(new PrintJob("2026-09-15", "RECON00002"));
        Assert.False(string.IsNullOrEmpty(jobId));

        await client.DisconnectAsync();
    }

    /// <summary>
    /// Auto-reconnect must not only notice the drop: it must actually restore a usable
    /// session. The simulator is torn down, a command is issued to force the timeout
    /// path, and then the simulator is brought back on the same port. The client must
    /// reconnect and accept a fresh job.
    /// </summary>
    [Fact]
    public async Task AutoReconnect_RecoversAfterServerRestart()
    {
        // [2026-09-17] The simulator is started on a port the OS picks, and the bound
        // port is read back afterwards. The previous form asked the OS for a free port,
        // closed that probe, and only then let the simulator bind - leaving a window in
        // which the port belonged to nobody and could be taken by another process. On
        // Windows SO_REUSEADDR makes that takeover silent, and the client would then
        // connect to the wrong listener and never be answered. Delegating the choice to
        // MockPrinter removes the window entirely.
        DominoMockServer.MockPrinter simulator =
            new DominoMockServer.MockPrinter(0, printDurationMs: 400, quiet: true);

        // [2026-09-17] Diagnostics: record BOTH sides of the wire so a failure says
        // which side went quiet. The client already exposes TrafficLogger and the
        // simulator already exposes LogLine, so no production code is touched.
        List<string> wire = new List<string>();

        Action<string> record = line =>
        {
            lock (wire)
            {
                wire.Add(line);
            }
        };

        simulator.LogLine += (sender, line) => record("SIM  " + line);

        simulator.Start();

        int port = simulator.Port;

        using DominoA200Client client = new DominoA200Client(
            "127.0.0.1",
            port,
            responseTimeoutMs: 500,
            autoReconnect: true,
            reconnectDelayMs: 300);

        client.TrafficLogger = record;

        TaskCompletionSource<bool> reconnecting =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        int disconnectedEvents = 0;

        client.OnDisconnected += (sender, args) => Interlocked.Increment(ref disconnectedEvents);
        client.OnReconnecting += (sender, args) => reconnecting.TrySetResult(true);

        await client.ConnectAsync();

        try
        {
            // [2026-09-16] Kill the peer while the client still believes it holds a live
            // connection, then issue a command whose answer can never arrive. This drives
            // the timeout / write-failure path that raises OnReconnecting and starts the
            // recovery loop - without relying on a fixed sleep for the loss to be seen.
            simulator.Stop();

            try
            {
                await client.SendPrintJobAsync(new PrintJob("2026-09-15", "TIMEOUT001"));
            }
            catch (Exception)
            {
                // A timeout (or a write failure) routes through the connection-loss handler.
            }

            Task raised = await Task.WhenAny(reconnecting.Task, Task.Delay(TimeSpan.FromSeconds(5)));

            Assert.True(raised == reconnecting.Task,
                "The client should have raised OnReconnecting after the link was lost.");
            Assert.True(Volatile.Read(ref disconnectedEvents) >= 1,
                "The client should have raised OnDisconnected.");

            // Bring the simulator back on the SAME port so recovery can succeed.
            //
            // [2026-09-17] Stop and Start happen inside the one call below, with no await
            // between them. That is the point: the failure this test was hitting was not
            // "the reconnect logic is broken" but "the port was unowned long enough for
            // another process to take it, and the client then reconnected to a listener
            // that never answers". Keeping teardown and rebind adjacent shrinks that
            // window to nothing, and the Start overload throws if the port could not be
            // reclaimed, so a takeover can no longer masquerade as a reconnect failure.
            simulator.Start(port);

            Assert.True(simulator.Port == port,
                "The simulator should have reclaimed port " + port.ToString()
                + " but is listening on " + simulator.Port.ToString() + ".");

            // [2026-09-17] Poll for the actual reconnection rather than sleeping a fixed
            // interval, so the assertion is event-driven.
            DateTime reconnectDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < reconnectDeadline && !client.IsConnected)
            {
                await Task.Delay(100);
            }

            // [2026-09-17] Diagnostics: on failure, attach the captured wire log so the
            // test output says WHY the client never came back - no separate run needed.
            if (!client.IsConnected)
            {
                string dump;
                lock (wire)
                {
                    dump = string.Join(Environment.NewLine, wire);
                }

                Console.WriteLine("--- capture: client never became connected ---");
                Console.WriteLine(dump);
                Console.WriteLine("--- end capture ---");
            }

            Assert.True(client.IsConnected,
                "The client should have reconnected after the simulator came back.");

            // The recovered session must be fully functional, not merely connected.
            string jobId = await client.SendPrintJobAsync(new PrintJob("2026-09-15", "RECOV00001"));
            Assert.False(string.IsNullOrEmpty(jobId),
                "A job submitted after recovery should be accepted and return an id.");
        }
        finally
        {
            // Guarantees the simulator is released even when an assertion fails above.
            simulator.Stop();
        }
    }

    /// <summary>
    /// A clean, caller-initiated disconnect must not be reported as a recoverable
    /// failure, otherwise every orderly shutdown would trigger a reconnect storm.
    /// </summary>
    [Fact]
    public async Task CleanDisconnect_DoesNotRaiseReconnecting()
    {
        using DominoA200Client client = new DominoA200Client(
            _fixture.Host,
            _fixture.Port,
            autoReconnect: true,
            reconnectDelayMs: 300);

        bool reconnectingRaised = false;
        client.OnReconnecting += (sender, args) => reconnectingRaised = true;

        await client.ConnectAsync();
        await client.DisconnectAsync();

        await Task.Delay(800);

        Assert.False(reconnectingRaised,
            "A deliberate disconnect is not a failure and must not start a reconnect loop.");
        Assert.False(client.IsConnected);
    }

    /// <summary>
    /// Without auto-reconnect, a command that can never be answered must be reported as
    /// a liveness failure: the client raises <see cref="OnDisconnected"/>, drops the
    /// connection, and surfaces the failure as <see cref="PrinterTimeoutException"/>.
    ///
    /// <para>
    /// <b>Why the simulator is put into silent mode instead of being stopped.</b>
    /// Stopping the server closes the socket, and the client's receive loop notices the
    /// FIN/RST within a few milliseconds and marks the link lost. A command issued after
    /// that is rejected with <see cref="InvalidOperationException"/> ("not connected")
    /// before it is ever written, so the timeout path is never reached and the assertion
    /// becomes a race against the receive loop.
    /// </para>
    ///
    /// <para>
    /// Silent mode keeps the connection open and simply never answers, which is exactly
    /// the real-world case this test is about: the printer is powered on and reachable,
    /// but has stopped responding. A dedicated simulator is used so putting it in silent
    /// mode cannot disturb the shared fixture the other tests rely on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CommandTimeout_WithoutAutoReconnect_RaisesDisconnected()
    {
        int port = FindFreePort();

        DominoMockServer.MockPrinter simulator =
            new DominoMockServer.MockPrinter(port, printDurationMs: 400, quiet: true);

        simulator.Start();

        try
        {
            using DominoA200Client client = new DominoA200Client(
                "127.0.0.1",
                port,
                responseTimeoutMs: 500,
                autoReconnect: false);

            int disconnectedEvents = 0;
            client.OnDisconnected += (sender, args) => Interlocked.Increment(ref disconnectedEvents);

            await client.ConnectAsync();
            Assert.True(client.IsConnected);

            // [2026-09-17] The link stays up but goes quiet, so the next command is
            // written successfully and then never answered - the genuine timeout case.
            simulator.Silent = true;

            await Assert.ThrowsAsync<PrinterTimeoutException>(async () =>
            {
                await client.SendPrintJobAsync(new PrintJob("2026-09-15", "TIMEOUT002"));
            });

            Assert.False(client.IsConnected,
                "A command timeout without auto-reconnect must close the connection.");
            // Assert.True is used rather than Assert.Equal because xUnit's Assert.Equal
            // takes no message argument (that is NUnit syntax).
            Assert.True(Volatile.Read(ref disconnectedEvents) == 1,
                "The client should have raised OnDisconnected exactly once.");
        }
        finally
        {
            simulator.Stop();
        }
    }

    /// <summary>
    /// Ask the OS for an unused loopback port. Needed by tests that bring their own
    /// simulator instance rather than using the shared fixture.
    /// </summary>
    private static int FindFreePort()
    {
        System.Net.Sockets.TcpListener probe =
            new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);

        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    /// <summary>
    /// The internal FIFO mirror must report full at capacity and empty after a clear.
    /// This exercises <see cref="JobQueue"/> directly, independently of the socket.
    /// </summary>
    [Fact]
    public void JobQueue_ReportsFullAtCapacityAndClears()
    {
        JobQueue queue = new JobQueue();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.IsFull);

        queue.Enqueue("JOB-000001", "A");
        queue.Enqueue("JOB-000002", "B");
        queue.Enqueue("JOB-000003", "C");

        Assert.Equal(3, queue.Count);
        Assert.True(queue.IsFull);

        // Completing the oldest must free a slot.
        JobQueueEntry? oldest = queue.DequeueOldest();
        Assert.NotNull(oldest);
        Assert.Equal("JOB-000001", oldest!.JobId);
        Assert.False(queue.IsFull);

        queue.Clear();
        Assert.Equal(0, queue.Count);
    }
}

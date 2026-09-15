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
    /// A command that is written but never answered must be treated as a lost link:
    /// the client raises its disconnect event and, with auto-reconnect enabled, begins
    /// trying to recover.
    ///
    /// <para>
    /// The drop is produced honestly — the simulator is stopped while the client holds
    /// the connection open, then a command is issued. The bytes go out to a socket
    /// whose peer has gone away and no answer ever returns, which is exactly the
    /// half-open case the timeout path exists to catch.
    /// </para>
    /// </summary>
    /// <summary>
    /// Auto-reconnect must not only notice the drop: it must actually restore a usable
    /// session. The simulator is torn down, a command is issued to force the timeout
    /// path, and then the simulator is brought back on the same port. The client must
    /// reconnect and accept a fresh job.
    /// </summary>
    [Fact]
    public async Task AutoReconnect_RecoversAfterServerRestart()
    {
        int port = FindFreePort();

        DominoMockServer.MockPrinter simulator =
            new DominoMockServer.MockPrinter(port, printDurationMs: 400, quiet: true);

        simulator.Start();

        using DominoA200Client client = new DominoA200Client(
            "127.0.0.1",
            port,
            responseTimeoutMs: 500,
            autoReconnect: true,
            reconnectDelayMs: 300);

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
            // recovery loop — without relying on a fixed sleep for the loss to be seen.
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
            simulator.Start();

            // [2026-09-16] Poll for the actual reconnection rather than sleeping a fixed
            // interval, so the assertion is event-driven.
            DateTime reconnectDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < reconnectDeadline && !client.IsConnected)
            {
                await Task.Delay(100);
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
    /// A dedicated simulator is used so stopping it does not disturb the shared fixture
    /// the other tests rely on.
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

            // Tear the peer down so the next command can never get a reply, then issue it.
            // [2026-09-16] A short settle after the stop lets the loss be observed before
            // the write, making the timeout path deterministic.
            simulator.Stop();
            await Task.Delay(200);

            await Assert.ThrowsAsync<PrinterTimeoutException>(async () =>
            {
                await client.SendPrintJobAsync(new PrintJob("2026-09-15", "TIMEOUT002"));
            });

            Assert.False(client.IsConnected,
                "A command timeout without auto-reconnect must close the connection.");
            // [2026-09-16 fix] xUnit's Assert.Equal takes no message argument (that is
            // NUnit syntax), so the count assertion goes through Assert.True instead.
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

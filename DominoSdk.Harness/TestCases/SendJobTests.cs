using DominoA200Sdk;
using DominoA200Sdk.Core;
using DominoA200Sdk.Exceptions;
using DominoA200Sdk.Models;
using DominoSdk.Harness.TestFixture;
using Xunit;

namespace DominoSdk.Harness.TestCases;

/// <summary>
/// Verifies job submission: the acceptance path (ACK, queue grows) and the rejection
/// path (NAK once the three-deep FIFO is full).
/// </summary>
[Collection("MockServer")]
public sealed class SendJobTests
{
    private readonly MockServerFixture _fixture;

    /// <summary>Receives the shared simulator fixture.</summary>
    public SendJobTests(MockServerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A single job should be acknowledged and reflected in the FIFO count.
    /// </summary>
    [Fact]
    public async Task SendPrintJob_IsAcknowledgedAndQueued()
    {
        using DominoA200Client client = new DominoA200Client(_fixture.Host, _fixture.Port);
        await client.ConnectAsync();

        PrintJob job = new PrintJob("2026-09-15", "TEST000001");
        string jobId = await client.SendPrintJobAsync(job);

        Assert.False(string.IsNullOrEmpty(jobId), "The SDK should return a job identifier.");
        Assert.True(await client.GetFifoQueueCountAsync() >= 1, "The job should be visible in the FIFO.");

        await client.DisconnectAsync();
    }

    /// <summary>
    /// Filling the queue to capacity then submitting one more job must raise
    /// <see cref="PrinterNackException"/>, because the simulator answers <c>0x15</c>
    /// exactly as the hardware does.
    ///
    /// <para>
    /// A dedicated simulator is constructed here with a very long print time, so no
    /// completion can fire and free a slot mid-test. Sharing the fast fixture would
    /// make this assertion race against the completion timer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SendPrintJob_RejectedWithNackWhenFifoIsFull()
    {
        // Print time far exceeds the duration of this test, so the queue stays full.
        using DominoMockServer.MockPrinter simulator =
            new DominoMockServer.MockPrinter(FindFreePort(), printDurationMs: 60000, quiet: true);

        simulator.Start();

        try
        {
            using DominoA200Client client = new DominoA200Client("127.0.0.1", simulator.Port);
            await client.ConnectAsync();

            // Fill all three slots. Each submission must be accepted.
            for (int i = 0; i < 3; i++)
            {
                await client.SendPrintJobAsync(new PrintJob("2026-09-15", "FILL" + i.ToString("D6")));
            }

            Assert.Equal(3, await client.GetFifoQueueCountAsync());

            // The fourth submission must be refused: the queue is saturated.
            await Assert.ThrowsAsync<PrinterNackException>(async () =>
            {
                await client.SendPrintJobAsync(new PrintJob("2026-09-15", "OVERFLOWX9"));
            });

            // [2026-09-16] A NAK rejection must also drive the client into the Alarm
            // state, because the receive loop reports the rejection via ReportAlarm.
            Assert.Equal(PrinterState.Alarm, client.State);

            await client.DisconnectAsync();
        }
        finally
        {
            simulator.Stop();
        }
    }

    /// <summary>
    /// Ask the OS for an unused loopback port, so a dedicated simulator does not
    /// collide with the shared fixture or with parallel runs.
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
    /// A job with an empty code value must be rejected by the model before any I/O.
    /// </summary>
    [Fact]
    public void PrintJob_RejectsEmptyCodeValue()
    {
        Assert.Throws<ArgumentException>(() => new PrintJob("2026-09-15", ""));
        Assert.Throws<ArgumentException>(() => new PrintJob("2026-09-15", "   "));
    }

    /// <summary>
    /// The payload should join date and code with a single space, and omit the date
    /// when it is empty.
    /// </summary>
    [Fact]
    public void PrintJob_BuildsExpectedPayload()
    {
        PrintJob withDate = new PrintJob("2026-09-15", "ABC123");
        Assert.Equal("2026-09-15 ABC123", withDate.ToPayload());

        PrintJob withoutDate = new PrintJob(string.Empty, "ABC123");
        Assert.Equal("ABC123", withoutDate.ToPayload());
    }
}

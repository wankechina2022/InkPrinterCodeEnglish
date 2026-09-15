using DominoA200Sdk;
using DominoA200Sdk.Models;
using DominoSdk.Harness.TestFixture;
using Xunit;

namespace DominoSdk.Harness.TestCases;

/// <summary>
/// Verifies that the unsolicited <c>0x32</c> print-complete event pushed by the printer
/// is received and surfaced through <see cref="DominoA200Client.OnJobCompleted"/>.
///
/// <para>
/// This is the test that proves the asynchronous half of the protocol works: the
/// client must not merely answer commands, it must also react to data the printer
/// sends unprompted.
/// </para>
/// </summary>
[Collection("MockServer")]
public sealed class JobCompleteEventTests
{
    private readonly MockServerFixture _fixture;

    /// <summary>Receives the shared simulator fixture.</summary>
    public JobCompleteEventTests(MockServerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Submitting one job should, after the simulated print delay, produce exactly one
    /// completion callback carrying the correlated job identifier.
    /// </summary>
    [Fact]
    public async Task OnJobCompleted_FiresAfterPrintDelay()
    {
        using DominoA200Client client = new DominoA200Client(_fixture.Host, _fixture.Port);

        TaskCompletionSource<PrinterEventArgs> completion =
            new TaskCompletionSource<PrinterEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.OnJobCompleted += (sender, args) =>
        {
            completion.TrySetResult(args);
        };

        await client.ConnectAsync();

        PrintJob job = new PrintJob("2026-09-15", "DONE000001");
        string jobId = await client.SendPrintJobAsync(job);

        // The simulator prints for 400 ms, so allow generous headroom.
        Task finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(finished == completion.Task, "The print-complete event did not arrive in time.");

        PrinterEventArgs received = await completion.Task;

        Assert.Equal(jobId, received.JobId);
        Assert.Contains("DONE000001", received.CodeValue);

        await client.DisconnectAsync();
    }

    /// <summary>
    /// The FIFO mirror should shrink to zero as completion events are consumed.
    ///
    /// <para>
    /// A dedicated simulator is used with a long print time so the "submitted" count can
    /// be observed without racing the completion timer; the print is then allowed to
    /// finish and the count is expected to return to zero.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FifoCount_DecreasesAfterCompletion()
    {
        using DominoMockServer.MockPrinter simulator =
            new DominoMockServer.MockPrinter(FindFreePort(), printDurationMs: 600, quiet: true);

        simulator.Start();

        try
        {
            using DominoA200Client client = new DominoA200Client("127.0.0.1", simulator.Port);

            int completedCount = 0;
            client.OnJobCompleted += (sender, args) => Interlocked.Increment(ref completedCount);

            await client.ConnectAsync();

            await client.SendPrintJobAsync(new PrintJob("2026-09-15", "DRAIN00001"));

            // Immediately after the ACK the job must be mirrored. The print takes 600 ms,
            // so this observation is not racing the completion.
            int afterSubmit = await client.GetFifoQueueCountAsync();
            Assert.Equal(1, afterSubmit);

            // Wait out the simulated print delay.
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && Volatile.Read(ref completedCount) == 0)
            {
                await Task.Delay(50);
            }

            Assert.Equal(1, Volatile.Read(ref completedCount));
            Assert.Equal(0, await client.GetFifoQueueCountAsync());

            await client.DisconnectAsync();
        }
        finally
        {
            simulator.Stop();
        }
    }

    /// <summary>
    /// Ask the OS for an unused loopback port for a dedicated simulator instance.
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
}

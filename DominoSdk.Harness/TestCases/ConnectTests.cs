using DominoA200Sdk;
using DominoA200Sdk.Core;
using DominoSdk.Harness.TestFixture;
using Xunit;

namespace DominoSdk.Harness.TestCases;

/// <summary>
/// Verifies that a TCP connection can be established and torn down cleanly, including
/// the protocol handshake that runs immediately after connect.
/// </summary>
[Collection("MockServer")]
public sealed class ConnectTests
{
    private readonly MockServerFixture _fixture;

    /// <summary>Receives the shared simulator fixture.</summary>
    public ConnectTests(MockServerFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The client should connect, complete the handshake and report a usable state.
    /// </summary>
    [Fact]
    public async Task Connect_EstablishesSessionAndCompletesHandshake()
    {
        using DominoA200Client client = new DominoA200Client(_fixture.Host, _fixture.Port);

        await client.ConnectAsync();

        Assert.True(client.IsConnected, "Client should report a live connection.");
        Assert.Equal(PrinterState.Idle, client.State);

        await client.DisconnectAsync();

        Assert.False(client.IsConnected, "Client should report disconnected after teardown.");
        Assert.Equal(PrinterState.Disconnected, client.State);
    }

    /// <summary>
    /// The handshake should issue the signal-setup and queue-clear commands, which the
    /// simulator answers with ACK. Asserting on the simulator's own log proves the
    /// exchange really happened rather than being inferred client-side.
    ///
    /// <para>
    /// A dedicated simulator is used here so the log belongs to this test alone; the
    /// shared fixture's log accumulates across the whole collection and would make the
    /// assertion depend on test ordering.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Connect_IssuesHandshakeCommands()
    {
        using DominoMockServer.MockPrinter simulator =
            new DominoMockServer.MockPrinter(FindFreePort(), printDurationMs: 400, quiet: true);

        List<string> log = new List<string>();
        simulator.LogLine += (sender, line) =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };

        simulator.Start();

        try
        {
            using DominoA200Client client = new DominoA200Client("127.0.0.1", simulator.Port);

            await client.ConnectAsync();

            // The handshake completes before ConnectAsync returns, so the log is already
            // written; a short settle avoids racing the logging callback itself.
            await Task.Delay(100);

            List<string> snapshot;
            lock (log)
            {
                snapshot = new List<string>(log);
            }

            Assert.Contains(snapshot, line => line.Contains("Print-signal setup accepted"));
            Assert.Contains(snapshot, line => line.Contains("queue", StringComparison.OrdinalIgnoreCase)
                                              && line.Contains("cleared", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Connecting to a port with nothing listening must fail rather than hang.
    /// </summary>
    [Fact]
    public async Task Connect_FailsWhenNothingIsListening()
    {
        // Port 1 is reserved and never bound by the simulator.
        using DominoA200Client client = new DominoA200Client("127.0.0.1", 1, connectTimeoutMs: 1500);

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
        Assert.False(client.IsConnected);
    }
}

using DominoMockServer;
using Xunit;

namespace DominoSdk.Harness.TestFixture;

/// <summary>
/// xUnit fixture that starts a <see cref="MockPrinter"/> before the tests run and
/// stops it afterwards.
///
/// <para>
/// <b>Why this matters.</b> It means the whole suite is self-contained: no printer on
/// the bench, no manually started process, no external port to remember. A reviewer
/// clones the repository and runs the tests; the simulator appears and disappears
/// around them.
/// </para>
///
/// <para>
/// <b>Port selection.</b> A free port is discovered by binding a temporary listener,
/// so parallel test runs on the same machine do not collide on a hard-coded 8001.
/// </para>
/// </summary>
public sealed class MockServerFixture : IDisposable
{
    private readonly MockPrinter _printer;

    /// <summary>Collects every traffic line the simulator logs, for assertions.</summary>
    public List<string> ServerLog { get; } = new List<string>();

    /// <summary>Starts the simulator on a free loopback port.</summary>
    public MockServerFixture()
    {
        Port = FindFreePort();

        // A short print time keeps the suite fast while still exercising the
        // asynchronous completion push.
        _printer = new MockPrinter(Port, printDurationMs: 400, quiet: true);
        _printer.LogLine += (sender, line) =>
        {
            lock (ServerLog)
            {
                ServerLog.Add(line);
            }
        };

        _printer.Start();
    }

    /// <summary>The port the simulator is listening on.</summary>
    public int Port { get; }

    /// <summary>The simulator host. Always loopback in the harness.</summary>
    public string Host
    {
        get { return "127.0.0.1"; }
    }

    /// <summary>Snapshot of the logged traffic, safe to read while the server runs.</summary>
    public IReadOnlyList<string> SnapshotLog()
    {
        lock (ServerLog)
        {
            return ServerLog.ToArray();
        }
    }

    /// <summary>Stop the simulator.</summary>
    public void Dispose()
    {
        _printer.Dispose();
    }

    /// <summary>
    /// Ask the OS for an unused TCP port by binding port 0 and reading back the
    /// assigned number.
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

/// <summary>
/// Shares one simulator instance across every test in a collection, so the suite does
/// not pay simulator start-up costs per test class.
/// </summary>
[CollectionDefinition("MockServer")]
public sealed class MockServerCollection : ICollectionFixture<MockServerFixture>
{
}

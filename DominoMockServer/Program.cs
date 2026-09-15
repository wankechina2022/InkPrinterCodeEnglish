using DominoMockServer;

// ============================================================
// Domino A200+ Mock Simulator
//
// Starts a TCP server that emulates a Domino A200+ inkjet printer so the SDK can be
// developed and demonstrated without physical hardware.
//
// Usage:  DominoMockServer [port] [--quiet]
//   port      TCP port to listen on (default 8001)
//   --quiet   suppress the traffic log
// ============================================================

int port = 8001;
bool quiet = false;

foreach (string arg in args)
{
    if (string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase))
    {
        quiet = true;
    }
    else if (int.TryParse(arg, out int parsedPort))
    {
        port = parsedPort;
    }
}

Console.WriteLine("==============================================================");
Console.WriteLine("  Domino A200+ Codenet Mock Simulator");
Console.WriteLine("--------------------------------------------------------------");
Console.WriteLine("  This is NOT official Domino software. It emulates the printer");
Console.WriteLine("  protocol for interoperability development and demonstration.");
Console.WriteLine("==============================================================");
Console.WriteLine();
Console.WriteLine("Starting mock printer on 127.0.0.1:" + port.ToString() + " ...");
Console.WriteLine("Emulated limits: FIFO capacity 3, print time 1500 ms.");
Console.WriteLine("All traffic is logged as raw hexadecimal bytes below.");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

using MockPrinter printer = new MockPrinter(port, quiet: quiet);

printer.LogLine += (sender, line) =>
{
    // Console output is already produced by MockPrinter; this subscription exists
    // purely to demonstrate that traffic is observable programmatically.
};

printer.Start();

// Park the main thread until the operator interrupts.
using ManualResetEventSlim shutdown = new ManualResetEventSlim(false);

Console.CancelKeyPress += (sender, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Set();
};

shutdown.Wait();

Console.WriteLine();
Console.WriteLine("Shutting down mock printer ...");
printer.Stop();
Console.WriteLine("Stopped.");

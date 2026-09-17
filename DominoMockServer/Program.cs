using DominoMockServer;

// ============================================================
// Domino A200+ Mock Simulator
//
// Starts a TCP server that emulates a Domino A200+ inkjet printer so the SDK can be
// developed and demonstrated without physical hardware.
//
// Usage:  DominoMockServer [port] [--quiet]
//   port      TCP port to listen on (default 7000)
//   --quiet   suppress the traffic log
// ============================================================

int port = 7000;
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
Console.WriteLine("Emulated limits: FIFO capacity " + MockFifoQueue.CAPACITY.ToString()
                  + ", print time " + MockPrinter.DEFAULT_PRINT_DURATION_MS.ToString() + " ms.");
Console.WriteLine("All traffic is logged as raw hexadecimal bytes below.");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

using MockPrinter printer = new MockPrinter(port, quiet: quiet);

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

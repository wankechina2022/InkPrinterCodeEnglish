using DominoA200Sdk;
using DominoA200Sdk.Exceptions;
using DominoA200Sdk.Models;

// ============================================================
// Domino A200+ Codenet SDK Demo
//
// A minimal console programme that shows the complete round trip:
//   connect -> submit jobs -> observe ACK/NACK -> receive the 0x32
//   print-complete event -> query the FIFO depth.
//
// Every byte exchanged is printed as raw hexadecimal, which is what makes the
// protocol behaviour visible to someone evaluating the SDK.
//
// Usage:
//   1. Start DominoMockServer in one terminal.
//   2. Run DemoConsoleApp in another.
//
// Options:  DemoConsoleApp [host] [port]     (defaults: 127.0.0.1 7000)
// ============================================================

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out int parsedPort) ? parsedPort : 7000;

Console.WriteLine("==============================================================");
Console.WriteLine("  Domino A200+ Codenet SDK — Console Demo");
Console.WriteLine("--------------------------------------------------------------");
Console.WriteLine("  Unofficial interoperability demo. NOT official Domino");
Console.WriteLine("  software. For technical demonstration only, not for resale.");
Console.WriteLine("==============================================================");
Console.WriteLine();

using DominoA200Client printer = new DominoA200Client(host, port, autoReconnect: true);

// Route the SDK's raw traffic log to the console. This is the line that makes the
// whole interaction legible: every TX and RX appears as hexadecimal bytes.
printer.TrafficLogger = line => Console.WriteLine("    " + line);

int completedJobs = 0;

printer.OnConnected += (sender, args) =>
{
    Console.WriteLine();
    Console.WriteLine("[event] Connected to " + host + ":" + port.ToString());
};

printer.OnDisconnected += (sender, args) =>
{
    Console.WriteLine();
    Console.WriteLine("[event] Disconnected.");
};

printer.OnJobCompleted += (sender, args) =>
{
    completedJobs++;
    Console.WriteLine();
    Console.WriteLine("[event] Job finished, JobId:" + args.JobId
                      + "  Code:" + args.CodeValue
                      + "  (completed so far: " + completedJobs.ToString() + ")");
};

printer.OnStateChanged += (sender, args) =>
{
    Console.WriteLine("[event] State: " + args.Previous.ToString() + " -> " + args.Current.ToString());
};

// ------------------------------------------------------------
// 1. Connect and run the protocol handshake.
// ------------------------------------------------------------
Console.WriteLine("[step 1] Connecting and running the handshake ...");

try
{
    await printer.ConnectAsync();
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("[error] Could not connect: " + ex.Message);
    Console.WriteLine("        Start DominoMockServer first, then run this demo again.");
    return;
}

Console.WriteLine("[step 1] Done. State: " + printer.State.ToString());
Console.WriteLine();

// ------------------------------------------------------------
// 2. Submit print jobs and watch the FIFO fill and drain.
// ------------------------------------------------------------
Console.WriteLine("[step 2] Submitting print jobs ...");
Console.WriteLine();

for (int i = 1; i <= 3; i++)
{
    PrintJob job = new PrintJob(
        DateTime.Now.ToString("yyyy-MM-dd"),
        "ABC" + i.ToString("D6"));

    try
    {
        string jobId = await printer.SendPrintJobAsync(job);
        int pending = await printer.GetFifoQueueCountAsync();

        Console.WriteLine("  -> Submitted " + job.CodeValue
                          + " as " + jobId
                          + "  |  Pending in FIFO: " + pending.ToString());
    }
    catch (PrinterNackException ex)
    {
        Console.WriteLine("  -> Job " + job.CodeValue + " was NACKed: " + ex.Message);
    }

    // Space the submissions so completions can interleave with new jobs.
    await Task.Delay(400);
}

// Wait for every job in this batch to report a 0x32 completion before we force an
// overflow. Waiting on the completion events (not a fixed delay) makes the overflow
// step deterministic: we only proceed once the FIFO is known to have drained.
// [2026-09-16] Robustness: the overflow no longer depends on the mock's print time.
DateTime drainDeadline = DateTime.UtcNow.AddSeconds(10);
while (DateTime.UtcNow < drainDeadline && completedJobs < 3)
{
    await Task.Delay(100);
}

Console.WriteLine("  -> Batch 1 done; " + completedJobs.ToString()
                  + " completion event(s) received, FIFO drained.");
Console.WriteLine();

// ------------------------------------------------------------
// 3. Deliberately overflow the queue to demonstrate 0x15 handling.
// ------------------------------------------------------------
Console.WriteLine("[step 3] Overflowing the queue to demonstrate NAK (0x15) handling ...");
Console.WriteLine();

// Submit three jobs back-to-back so all three land in the FIFO, then a fourth that
// must be refused because the on-board queue is already full (capacity 3). This is
// deterministic: after the drain above the FIFO starts empty, the three quick
// submissions saturate it, and the fourth is NAKed.
// [2026-09-16] Robustness: the overflow no longer relies on mock print-time timing.
for (int i = 1; i <= 3; i++)
{
    PrintJob job = new PrintJob(DateTime.Now.ToString("yyyy-MM-dd"), "FILL" + i.ToString("D6"));

    try
    {
        string jobId = await printer.SendPrintJobAsync(job);
        int pending = await printer.GetFifoQueueCountAsync();

        Console.WriteLine("  -> Submitted " + job.CodeValue
                          + " as " + jobId
                          + "  |  Pending in FIFO: " + pending.ToString());
    }
    catch (PrinterNackException ex)
    {
        Console.WriteLine("  -> Job " + job.CodeValue + " was NACKed: " + ex.Message);
    }
}

Console.WriteLine();

PrintJob overflowJob = new PrintJob(DateTime.Now.ToString("yyyy-MM-dd"), "OVERFLOW001");

try
{
    string jobId = await printer.SendPrintJobAsync(overflowJob);
    Console.WriteLine("  -> Unexpectedly accepted (" + jobId + "); the queue had room.");
}
catch (PrinterNackException ex)
{
    Console.WriteLine("  -> Expected rejection caught: PrinterNackException");
    Console.WriteLine("     " + ex.Message);
}

Console.WriteLine();

// ------------------------------------------------------------
// 4. Wait for the outstanding 0x32 completion events to arrive.
// ------------------------------------------------------------
Console.WriteLine("[step 4] Waiting for print-complete (0x32) events ...");

DateTime waitDeadline = DateTime.UtcNow.AddSeconds(8);

while (DateTime.UtcNow < waitDeadline && printer.GetStatus().FifoQueueCount > 0)
{
    await Task.Delay(200);
}

int remaining = (await printer.GetFifoQueueCountAsync());

Console.WriteLine();
Console.WriteLine("  -> Completed jobs this run: " + completedJobs.ToString());
Console.WriteLine("  -> Still pending in FIFO   : " + remaining.ToString());
Console.WriteLine();

// ------------------------------------------------------------
// 5. Final status snapshot and clean shutdown.
// ------------------------------------------------------------
Console.WriteLine("[step 5] Final status: " + printer.GetStatus().ToString());

await printer.DisconnectAsync();

Console.WriteLine("[step 5] Disconnected.");
Console.WriteLine();
Console.WriteLine("==============================================================");
Console.WriteLine("  Demo complete.");
Console.WriteLine("==============================================================");

# DominoA200Sdk

A small .NET 8 client library for the Domino A200+ inkjet printer **Codenet** protocol,
with **TCP/IP and RS232 serial** transports behind one shared framing layer: frame
assembly/parsing, ACK/NAK handling, timeouts, an on-board FIFO mirror, unsolicited
`0x32` print-complete events and automatic reconnect — all behind a small async API.

```csharp
using DominoA200Sdk;
using DominoA200Sdk.Models;

using DominoA200Client printer = new DominoA200Client("192.168.1.50", 7000, autoReconnect: true);

printer.OnJobCompleted += (sender, args) =>
    Console.WriteLine($"printed {args.CodeValue} ({args.JobId})");

await printer.ConnectAsync();
string jobId = await printer.SendPrintJobAsync(new PrintJob("2026-09-17", "ABC000123"));
await printer.DisconnectAsync();
```

The code is validated before it reaches the transport: a null/blank value, more than 9999
characters, a non-ASCII character, or the frame terminator `0x04` all throw
`ArgumentException` immediately rather than producing a frame the printer cannot parse.
See [Job payload rules](#job-payload-rules).

> ⚠️ **Not the official Domino SDK.** The protocol was obtained by reverse engineering
> captured network traffic; this library is an interoperability study published for
> learning and technical verification. It is not affiliated with, endorsed by or
> supported by Domino Printing Sciences plc, and must not be resold or shipped as a
> product. For production integration, obtain the vendor SDK.
>
> **本协议为网络抓包逆向分析所得，本项目仅用于学习与技术验证，不得用于商业用途。**
> Full statement: [../LICENSE](../LICENSE).

---

## Contents

| Section | What's in it |
|---|---|
| [Can this be used on its own?](#can-this-be-used-on-its-own) | Dependencies and the five-project relationship. |
| [Referencing it](#referencing-it) | Project reference, built assembly, local NuGet package. |
| [Minimal working example](#minimal-working-example) | TCP, end to end. |
| [RS232 serial transport](#rs232-serial-transport) | The same API over a COM port. |
| [Calling methods (public API)](#calling-methods-public-api) | Every method, property and event. |
| [Lifecycle and threading rules](#lifecycle-and-threading-rules) | The rules that separate "works" from "drops jobs". |
| [Constructor parameters](#constructor-parameters-dominoa200client) | Defaults and meanings. |
| [What throws, and what to do about it](#what-throws-and-what-to-do-about-it) | Exception-to-action table. |
| [Job payload rules](#job-payload-rules) | Frame layout, the length field, and what a code may contain. |
| [Length-aware framing](#length-aware-framing) | How `OE` frames are measured and validated. |
| [FIFO mirror and `0x32` correlation](#fifo-mirror-and-0x32-correlation) | Backpressure and completion events. |
| [Diagnostics](#diagnostics) | `TrafficLogger` and status snapshots. |
| [Trying it without hardware](#trying-it-without-hardware) | Mock, demo and the framing test matrix. |
| [Limits and known gaps](#limits-and-known-gaps) | What this SDK does **not** do. |

---

## Can this be used on its own?

**Yes.** The library has no *project* dependencies and a single package dependency:
`System.IO.Ports`, which provides the `SerialPort` type used by the RS232 transport.

| Project | Target | References | Role |
|---|---|---|---|
| **`DominoA200Sdk`** | `net8.0` | `System.IO.Ports` only — no `ProjectReference` | the library |
| `DominoMockServer` | `net8.0` | nothing (it re-implements the printer side) | simulator, no hardware needed |
| `DemoConsoleApp` | `net8.0` | `DominoA200Sdk` | end-to-end demo |
| `DominoSdk.Harness` | `net8.0` | `DominoA200Sdk`, `DominoMockServer`, xUnit | automated tests |
| `InkPrinterCode` | `net8.0-windows` | Microsoft.Data.Sqlite, NPOI, System.IO.Ports | the WinForms host application |

`InkPrinterCode` is **not** part of the library's dependency chain, in either
direction: it does not reference `DominoA200Sdk`, and `DominoA200Sdk` does not
reference it. The WinForms host keeps its own protocol/transport layer
(`Common/CodeNetProtocol.cs`, `Common/TcpPrinterConnection.cs`); this SDK is the
cleaned-up extraction of that same work, sharing no code with it. The five projects
sit in one solution for convenience only — you can copy the `DominoA200Sdk` folder
into any other solution and build it there.

---

## Referencing it

### 1. Project reference (inside this repository)

```xml
<ItemGroup>
  <ProjectReference Include="..\DominoA200Sdk\DominoA200Sdk.csproj" />
</ItemGroup>
```

### 2. Built assembly (no project reference)

```bash
dotnet build DominoA200Sdk/DominoA200Sdk.csproj -c Release
```

Then add a reference to

```
DominoA200Sdk/bin/Release/net8.0/DominoA200Sdk.dll     ← the library
DominoA200Sdk/bin/Release/net8.0/DominoA200Sdk.xml     ← XML docs, gives IntelliSense
```

### 3. Local NuGet package (private feed only)

```bash
dotnet pack DominoA200Sdk/DominoA200Sdk.csproj -c Release -o ./artifacts
dotnet nuget add source "$(pwd)/artifacts" -n domino-local
dotnet add package DominoA200Sdk --version 1.0.0
```

Keep the package on a private/local feed — publishing an unofficial SDK to a public
feed would misrepresent its origin.

### Target framework

The library targets `net8.0`, so the host project must target `net8.0` or later
(`net8.0-windows` is fine — that is what `InkPrinterCode` uses). A `net6.0` host
cannot reference it as-is; multi-target the SDK (`<TargetFrameworks>net6.0;net8.0</TargetFrameworks>`)
if you really need that — the source uses no .NET 8-only API, though that combination
is untested here.

---

## Minimal working example

```csharp
using DominoA200Sdk;
using DominoA200Sdk.Exceptions;
using DominoA200Sdk.Models;

// One client per printer endpoint: it owns the socket, the receive thread,
// the FIFO mirror and the state machine. Create it once and reuse it.
using DominoA200Client printer = new DominoA200Client(
    host:              "192.168.1.50",   // printer IP or host name (required)
    port:              7000,             // Codenet TCP port
    responseTimeoutMs: 3000,             // wait for 0x06 / 0x15 before timing out
    connectTimeoutMs:  5000,
    autoReconnect:     true,             // recover from a lost link in the background
    reconnectDelayMs:  2000);

// Optional: raw TX/RX as hex -- invaluable when a printer misbehaves.
printer.TrafficLogger = line => Console.WriteLine(line);

// Subscribe BEFORE submitting; a completion can arrive very quickly.
printer.OnJobCompleted += (sender, e) =>
    Console.WriteLine($"finished JobId={e.JobId} code={e.CodeValue} at {e.CompletedAt:HH:mm:ss}");

printer.OnConnected    += (sender, e) => Console.WriteLine("connected");
printer.OnDisconnected += (sender, e) => Console.WriteLine("disconnected");
printer.OnReconnecting += (sender, e) => Console.WriteLine("reconnecting ...");

await printer.ConnectAsync();                       // socket + handshake

try
{
    string jobId = await printer.SendPrintJobAsync(new PrintJob("2026-09-17", "ABC000123"));
    int pending  = await printer.GetFifoQueueCountAsync();
    Console.WriteLine($"{jobId} queued, {pending} awaiting completion");
}
catch (PrinterNackException ex)                     // 0x15 -- queue full, routine
{
    Console.WriteLine("refused: " + ex.Message);
}
catch (PrinterTimeoutException ex)                  // no answer -- link treated as lost
{
    Console.WriteLine("no answer: " + ex.Message);
}

await printer.DisconnectAsync();
```

`ConnectAsync()` performs the handshake for you: enable print-complete notification
(`1B 49 31 32 04`) and clear the TCP/IP, RS232 and history queues. Skip it and the
printer prints silently, never sending a completion event.

---

## RS232 serial transport

The same protocol is also available over a serial port. `DominoA200SerialClient`
reuses the **exact same** `CodenetFrame` framing, `JobQueue` mirror and
`PrinterStateMachine` as the TCP client — only the byte transport differs — so the
public surface is identical and switching a printer from TCP to serial is a one-line
constructor change.

```csharp
using DominoA200Sdk;
using DominoA200Sdk.Models;

// Connect over COM3 at 9600 baud, 8 data bits / 1 stop bit / no parity (8N1).
using DominoA200SerialClient printer = new DominoA200SerialClient(
    portName:       "COM3",
    baudRate:       9600,
    autoReconnect:  true);

printer.TrafficLogger   = line => Console.WriteLine(line);
printer.OnJobCompleted += (s, e) =>
    Console.WriteLine($"finished {e.CodeValue} ({e.JobId})");

await printer.ConnectAsync();
string jobId = await printer.SendPrintJobAsync(new PrintJob("2026-09-17", "ABC000123"));
await printer.DisconnectAsync();
```

**Serial specifics**
* The link opens **8N1** (`Parity.None`, 8 data bits, 1 stop bit) at the given baud rate
  (default 9600; 19200 / 38400 / 57600 / 115200 are all valid).
* The `SerialPort` object is guarded by a single I/O lock so the receive thread and the
  caller's writes never touch the port concurrently — the same proven pattern used by the
  field-tested serial connection in the `InkPrinterCode` host.
* A short read-timeout polling loop keeps the receive thread responsive to disconnect and
  to `DisconnectAsync()`; closing the port makes the blocked read throw, which ends the loop.
* Everything else — the handshake, ACK/NAK/`0x32` handling, the FIFO mirror and the
  reconnect loop — is identical to the TCP client.

---

## Calling methods (public API)

Everything the caller touches. The SDK owns the transport, the receive thread, the FIFO
mirror and the state machine; you drive it through these members only.
`DominoA200SerialClient` exposes the **same methods, properties and events** as
`DominoA200Client` — the two differ only in their constructors and in the two endpoint
properties noted below.

**TCP constructor** — `new DominoA200Client(host, port = 7000, responseTimeoutMs = 3000,
connectTimeoutMs = 5000, autoReconnect = false, reconnectDelayMs = 2000)`.

**Serial constructor** — `new DominoA200SerialClient(portName, baudRate = 9600,
responseTimeoutMs = 3000, connectTimeoutMs = 5000, autoReconnect = false,
reconnectDelayMs = 2000)`. `connectTimeoutMs` is accepted for signature parity only:
`SerialPort.Open()` is synchronous and there is no connect timeout to apply.

**Methods**

| Method | Returns | What it does |
|---|---|---|
| `ConnectAsync(CancellationToken)` | `Task` | Opens the transport (the TCP socket, or the serial port), runs the handshake (print-signal setup `1B 49 31 32 04` + clear queues) and starts the receive loop. Nothing is ever written to a control port. |
| `SendPrintJobAsync(PrintJob, ct)` | `Task<string>` | Frames and sends one code. On `0x06` ACK it records the job in the FIFO mirror and returns the generated `JobId`. On `0x15` NAK it throws `PrinterNackException` (queue full). |
| `GetFifoQueueCountAsync(ct)` | `Task<int>` | Returns the **client-side mirror count**; also sends the depth query as best-effort (the printer reply, if any, is ignored). |
| `GetStatus()` | `PrinterStatus` | Synchronous snapshot: `IsConnected`, `FifoQueueCount`, `StateText`. |
| `DisconnectAsync()` | `Task` | Forced close — `Shutdown(Both)` → `LingerOption(true, 0)` → `Close` (RST), identical to the field `TcpPrinterConnection`. |
| `Dispose()` | `void` | Releases everything; prefer `using`. |

**Properties / diagnostics**

| Member | Type | Note |
|---|---|---|
| `IsConnected` | `bool` | Transport up. |
| `State` | `PrinterState` | `Disconnected \| Idle \| Printing \| Alarm`. |
| `Host` / `Port` | `string` / `int` | Configured TCP endpoint (TCP client only). |
| `PortName` / `BaudRate` | `string` / `int` | Configured serial endpoint (serial client only). |
| `TrafficLogger` | `Action<string>?` | Raw TX/RX hex per line; `null` to disable. |

**Events** (all fire on background threads — marshal to the UI thread before touching controls)

| Event | Args | Fires when |
|---|---|---|
| `OnConnected` | — | Link established (your thread or the reconnect thread). |
| `OnDisconnected` | — | Link torn down. |
| `OnJobCompleted` | `PrinterEventArgs` (`JobId`, `CodeValue`, `CompletedAt`) | A bare `0x32` arrival is correlated to the **oldest** mirrored job (see below). |
| `OnStateChanged` | `PrinterStateChangedEventArgs` | High-level state transition. |
| `OnReconnecting` | — | A lost link is about to be retried (`autoReconnect` only). |

---

## Lifecycle and threading rules

The SDK is safe to call from several threads, but a few rules make the difference
between "works" and "mysteriously drops jobs":

| Rule | Why |
|---|---|
| **One client per printer endpoint**, created once and reused. | The socket, receive thread, FIFO mirror and state machine all belong to the instance. |
| **`ConnectAsync()` is re-entrant-safe.** Calling it while connected or while a connect is in flight returns immediately instead of opening a second socket. | A user call racing the background reconnect cannot duplicate the session. |
| **Commands are serialised internally** (one in flight at a time). Parallel `SendPrintJobAsync` calls queue up; they do not go faster. | The printer answers with a bare `0x06` carrying no command id, so two outstanding commands would make the answer ambiguous. |
| **Hold the submission rhythm the hardware can take** (the demo uses ~400 ms spacing). Back-to-back bursts simply fill the 3-deep queue and start returning `0x15`. | Normal flow control, not an error — `PrinterNackException` is routine at line rates. |
| **All events fire on background threads** — `OnJobCompleted` on the receive thread, `OnReconnecting` on the thread that noticed the loss. Marshal to the UI thread (`Control.Invoke` / `BeginInvoke`) before touching controls. | `OnConnected` may fire on your calling thread *or* on the reconnect thread. |
| **`DisconnectAsync()` is effectively synchronous** — it blocks up to ~1 s while joining the receive thread, then returns a completed task. Do not call it from the UI thread if that second matters. | The receive thread must be joined before the socket is released. |
| **`Dispose()` is final.** After it the client will not reconnect and will not be revived by an in-flight reconnect attempt. Use `using`. | Prevents a disposed client from silently holding a live socket. |
| **`autoReconnect: false` (default) leaves recovery to you.** | With `true`, a read failure, a graceful FIN, a failed write or an ACK timeout starts one background retry loop (`reconnectDelayMs` apart) until it succeeds. |

---

## Constructor parameters (`DominoA200Client`)

| Parameter | Default | Meaning |
|---|---|---|
| `host` | — | Printer host name or IP. Required, non-blank. |
| `port` | `7000` | Codenet TCP port. |
| `responseTimeoutMs` | `3000` | How long to wait for `0x06` / `0x15`. Also applied as the socket's send/receive timeout. |
| `connectTimeoutMs` | `5000` | TCP connect timeout. |
| `autoReconnect` | `false` | Background reconnect after a lost link. |
| `reconnectDelayMs` | `2000` | Delay between reconnect attempts. |

Values `<= 0` fall back to the defaults above.

`DominoA200SerialClient` takes `portName` / `baudRate` instead of `host` / `port`; its
full signature is listed under [Calling methods](#calling-methods-public-api).

---

## What throws, and what to do about it

| Exception | Raised when | Handling |
|---|---|---|
| `PrinterNackException` | The printer answers `0x15`. For a job this means the on-board FIFO (3 deep) is full. | Routine flow control: hold the job and retry shortly. |
| `PrinterTimeoutException` | No answer within `responseTimeoutMs`; also thrown when the write itself fails. Either way the SDK marks the link as lost first (auto-reconnect engages when enabled). | Check the cable/network, then reconnect. |
| `InvalidOperationException` | `SendPrintJobAsync` / `GetFifoQueueCountAsync` called while not connected. | Call `ConnectAsync()` first; with auto-reconnect, wait for `OnConnected`. |
| `SocketException`, `TimeoutException` | `ConnectAsync` could not reach the printer, or exceeded `connectTimeoutMs`. | Verify IP/port; a printer that reports "maximum connections reached" needs its old sockets to time out. |
| `ArgumentException` / `ArgumentNullException` | Bad input: blank host; null job; code value null/blank, longer than 9999 characters, containing non-ASCII characters, or containing the frame terminator `0x04`. Validated **before** anything reaches the transport — see [Job payload rules](#job-payload-rules). | Validate at the edge, before submitting; `CodenetFrame.ValidatePrintJobPayload` is public if you want to check codes while importing them. |

---

## Job payload rules

`new PrintJob(dateText, codeValue)` renders `"<date> <code>"` (`ToPayload()`), or just
the code when the date is empty, and the SDK frames it as

```
1B 4F 45 <4-digit length> <ASCII payload> 04
│        │                │             └─ EOT terminator
│        │                └─ the code text (ASCII only)
│        └─ zero-padded decimal count of the code text ONLY
└─ ESC 'O' 'E'
```

**The length field counts the code text alone** — not the four length digits and not the
terminator. A 20-character code therefore produces a 28-byte frame (`3 + 4 + 20 + 1`),
because the payload it measures is `"0020" + 20 chars`:

```
1B 4F 45 30 30 32 30 32 30 32 36 2D 30 39 2D 31 37 20 41 42 43 30 30 30 30 30 31 04
│        │        └──────────── 20 bytes of code text ────────────┘              │
│        └─ "0020" = 20, the count of the code text                              │
└─ 1B 4F 45 = ESC 'O' 'E'                                                        └─ 04
```

Getting this offset wrong by four is the easiest way to build a frame the printer
rejects, so the SDK does not leave it to you: `CodenetFrame.ValidatePrintJobPayload` runs
**before** anything is written to the transport.

### What a payload may contain

| Constraint | Why |
|---|---|
| Non-null, non-empty | An empty `OE` frame carries no code to print. |
| At most **9999** characters | The length field is four decimal digits. |
| **ASCII only** (each char `<= 0x7F`) | The printer expands the text with its own template; there is no defined encoding for anything else. |
| **Must not contain `0x04`** | `0x04` is the frame terminator. A payload byte of `0x04` makes the frame look finished early: the tail of the code is silently dropped and the remainder is re-scanned as stray bytes. Rejecting it up front is far safer than losing codes on a production line. |

`0x1B` (ESC) **is** allowed inside a payload — it is only special as the *first* byte of a
frame, so an escaped byte elsewhere is unambiguous.

`ValidatePrintJobPayload` is public, so you can pre-validate at the edge (while scanning
or importing codes, say) instead of discovering the problem at submit time. All four
violations throw `ArgumentException` naming the offending character or length.

```csharp
try
{
    CodenetFrame.ValidatePrintJobPayload(code);   // fail fast, before ConnectAsync
}
catch (ArgumentException ex)
{
    Console.WriteLine("code rejected: " + ex.Message);
}
```

The SDK does **not** wrap the value in a barcode or QR envelope — that is the printer
template's job. Layout, font and barcode encapsulation all live on the printer side.

---

## Length-aware framing

A receiver that finds frame boundaries by scanning for the first `0x04` is wrong for the
`OE` family, for exactly the reason above: the declared length and the scanned terminator
can disagree.

`CodenetFrame.TryParseFrame` therefore cross-checks both when an `OE` frame arrives:

1. Scan for the first `0x04` — this fallback bound is always computed.
2. If the header is `1B 4F 45`, read the four bytes that follow as a decimal length.
3. The terminator **must** sit at `3 + 4 + declared`. If it does not, the parser reports
   "need more bytes" rather than consuming a frame whose payload it cannot trust.
4. If those four bytes are **not** a usable length field, fall back to the scanned `0x04`.

Step 4 matters because not every `OE` frame is length-prefixed. The FIFO depth query
(`1B 4F 45 30 30 30 31 37 04`) and the clear-queue command
(`1B 4F 45 30 30 30 30 <idx> 04`) are fixed-layout literals, so the parser classifies them
as "no length field" and treats the scanned terminator as the bound. Step 3 is what
protects you from a corrupt or truncated length: rather than cutting the frame at a
boundary it cannot justify, the SDK waits for more bytes.

| Frame | Length field? | Bound used |
|---|---|---|
| Print job (`OE` + 4 digits + code + `04`) | yes | declared length, cross-checked against the terminator |
| FIFO query (`00017`) | no — literal | scanned `0x04` |
| Clear queue (`0000` + index) | no — literal | scanned `0x04` |
| Signal setup (`1B 49 31 32 04`) | no | scanned `0x04` |

What this buys you in practice:

* **A lying or corrupt length cannot truncate a job** — the parser refuses to guess.
* **Coalesced frames split in order**, and **split frames reassemble**: every split point
  of every frame type is covered by the test suite.
* **Stray garbage is resynchronised** instead of poisoning the stream.

Byte-level detail and worked examples: [../Docs/ProtocolNotes.md](../Docs/ProtocolNotes.md)
(§1.2 and §5.4).

---

## FIFO mirror and `0x32` correlation

Three facts drive the design, and they are the reason this integration is not a plain
request/response wrapper:

1. **The on-board queue holds 3 jobs.** The fourth submission is refused with `0x15`.
2. **Completion events are unsolicited and anonymous.** A bare `0x32` arrives with no
   job id, so it is correlated against the oldest entry of the client-side mirror.
3. **The mirror is not the printer's truth.** `GetFifoQueueCountAsync()` sends the
   depth query, tolerates a missing or refused reply, and returns the mirrored count.
   The mirror is cleared when the connection closes, and any in-flight ids are dropped
   with a `WARN` line — so treat the count as accounting, not as a synchronised gauge.

Repeated or uncorrelated `0x32` bytes (for example after a reconnect) still raise
`OnJobCompleted`, but with an empty `JobId` / `CodeValue`, and log a warning.

### How `0x32` reaches the caller — and what to do with it

The wire byte `0x32` is a **single-byte, payload-less** event. It is consumed by the
receive splitter (`ProcessBytes`) and routed to `RaiseJobCompleted()`, which:

1. dequeues the **oldest** entry from the client-side FIFO mirror;
2. builds `PrinterEventArgs(JobId, CodeValue, CompletedAt)` from *that mirror entry* —
   the printer never tells us which code finished, so those values are SDK-inferred;
3. invokes `OnJobCompleted` on the **receive thread** — an asynchronous *push*, not a
   return value of `SendPrintJobAsync`.

So the event is a **notification that a slot freed up**, not a value your `await` hands
back. The real "can I send again?" signal is the mirror count dropping by one — which
is exactly what `GetFifoQueueCountAsync()` reads.

### Recommended backpressure pattern

The SDK does **not** auto-throttle: `SendPrintJobAsync` will fill the 3-deep queue and
then return `PrinterNackException` on the fourth. To get the field-app rhythm
("keep sending, pause on `0x15`, resume on `0x32`"), gate the submission with a
capacity-3 semaphore released by the completion event:

```csharp
using DominoA200Client printer = new("192.168.1.50", 7000, autoReconnect: true);

// Gate capacity == printer FIFO capacity (3). Start full; each 0x32 releases one slot.
var gate = new SemaphoreSlim(3, 3);

printer.OnJobCompleted += (s, e) =>
{
    Console.WriteLine($"done {e.CodeValue} ({e.JobId})");
    gate.Release();                       // 0x32 arrived -> a slot is free again
};

await printer.ConnectAsync();

int i = 0;
while (true)
{
    await gate.WaitAsync();              // blocks when all 3 slots are in flight
    try
    {
        await printer.SendPrintJobAsync(
            new PrintJob(DateTime.Now.ToString("yyyy-MM-dd"),
                         "ABC" + (++i).ToString("D6")));
    }
    catch (PrinterNackException)          // 0x15 -- full (rare race); give the slot back
    {
        gate.Release();
        await Task.Delay(100);
    }
}
```

This keeps up to three jobs in flight, pauses on overflow, and resumes the moment a
`0x32` lands — with the SDK's FIFO mirror as the authoritative in-flight counter.
`DemoConsoleApp` ships the simpler fixed-batch version; the loop above is the pattern to
copy for continuous production printing.

---

## Diagnostics

```csharp
printer.TrafficLogger = line => File.AppendAllText("printer.log", line + Environment.NewLine);
```

Every line looks like

```
[08:14:22.325] [TX     ] 1B 4F 45 30 30 30 37 41 42 43 30 30 30 30 30 31 04
[08:14:22.334] [RX     ] 06
[08:14:24.401] [RX     ] 32
[08:14:24.402] [FRAME  ] ...
```

A throwing sink never breaks the receive path. Set `TrafficLogger = null` to disable.

Useful state for a status bar or log line:

```csharp
PrinterStatus status = printer.GetStatus();   // IsConnected / FifoQueueCount / StateText
PrinterState  state  = printer.State;         // Disconnected | Idle | Printing | Alarm
bool          up     = printer.IsConnected;
```

---

## Trying it without hardware

```bash
# terminal 1 — emulated A200+ (FIFO capacity 3, 1500 ms print time)
dotnet run --project DominoMockServer                 # optional: [port] [--quiet]

# terminal 2 — the demo drives connect / submit / NAK / 0x32 / disconnect
dotnet run --project DemoConsoleApp                   # optional: [host] [port]

# the automated suite starts its own simulator on a free port
dotnet test
```

The mock is only a simulator: it emulates the admission rule and the command grammar,
nothing else. Treat a green suite as proof that the *client* behaves, not that a
particular firmware revision will.

Two properties of the mock are worth knowing when you write tests against it:

* **It serves one session at a time**, matching a physical printer with a single Codenet
  port. A second client that connects while the first is still open is not refused — it
  waits to be accepted, so a test that forgets to disconnect will stall every connection
  that follows. Always `DisconnectAsync()` (or `using`) before the next client connects.
* **`Stop()` closes the link; `Silent = true` only goes quiet.** The first looks to a
  client like a dropped cable and is noticed within milliseconds, so a command issued
  afterwards fails as "not connected" rather than timing out. To exercise the
  command-timeout path, hold the connection open and set `Silent = true`.

### What the framing tests pin down

`DominoSdk.Harness/TestCases/FramingTests.cs` locks in the byte-level contract this
README describes — it needs no printer and no mock, because `CodenetFrame` is pure:

| Test | Asserts |
|---|---|
| `BuildPrintJobFrame_LengthFieldCountsCodeTextOnly` | The 4-digit field counts the code text only, not the digits themselves. |
| `BuildPrintJobFrame_RejectsTerminatorInsidePayload` | `0x04` anywhere in the code throws instead of producing a truncatable frame. |
| `BuildPrintJobFrame_AllowsEscInsidePayload` | `0x1B` mid-payload is still legal. |
| `BuildPrintJobFrame_RejectsInvalidPayload` | Null, empty and non-ASCII codes all throw. |
| `BuildPrintJobFrame_EnforcesMaximumLength` | 9999 characters is accepted, 10000 is not. |
| `TryParseFrame_RoundTripsEveryFrameType` | Every builder output parses back byte-identical. |
| `TryParseFrame_HandlesEverySplitPoint` | A frame cut at *any* byte offset still reassembles. |
| `TryParseFrame_RejectsLyingLengthField` | A declared length that disagrees with the terminator is refused, not guessed. |
| `TryParseFrame_SplitsCoalescedFramesInOrder` | Two frames in one read split correctly and in order. |
| `TryParseFrame_ResynchronisesAfterGarbage` | Leading junk does not poison the stream. |

---

## Requirements

* **.NET 8 SDK** to build, **.NET 8 runtime** to run (`Microsoft.NETCore.App` 8.x;
  the WinForms host additionally needs `Microsoft.WindowsDesktop.App` 8.x).
* One package, **`System.IO.Ports` 8.0.0**, restored automatically. It supplies the
  `SerialPort` type used by the RS232 transport; the TCP client needs nothing beyond
  the base class library.
* The demo, mock and test projects are console/test apps targeting `net8.0`.

---

## Limits and known gaps

* **Unofficial protocol.** Every byte format here was derived from packet captures of
  one A200+ integration; other firmware revisions or printer configurations may differ.
* **Print head 1 only** — the signal-setup frame used by `ConnectAsync` addresses head 1.
* **The printer's true queue depth is never parsed**; the SDK reports its own mirror
  (see above).
* **Serial parity is untested on hardware.** `DominoA200SerialClient` reuses the same
  framing, FIFO mirror and state machine as the TCP client, but only the TCP path has
  been exercised against a physical printer.
* **`0x04` is rejected everywhere in a payload.** The frame has a single terminator and no
  escape mechanism, so a code that legitimately needs a `0x04` byte cannot be sent as-is;
  it would have to be encoded at the printer-template level.
* **No TLS** — the Codenet port is a plain TCP socket, so keep it on a trusted network.
* **No delivery guarantee.** A job that the printer accepted but never printed is only
  observable as a missing `0x32`; there is no re-print from the SDK.

---

## More documentation

| Document | Contents |
|---|---|
| [../Docs/ApiReference.md](../Docs/ApiReference.md) | Every public type, member and default. |
| [../Docs/ProtocolNotes.md](../Docs/ProtocolNotes.md) | Observed wire behaviour with byte-level examples; §1.2 covers the `OE` frame layout, §5.4 length-aware framing. |
| [../README.md](../README.md) | Repository overview: mock, demo, tests, layout. |

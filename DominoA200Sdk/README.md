# DominoA200Sdk

A dependency-free .NET 8 client library for the Domino A200+ inkjet printer
**Codenet** protocol: TCP transport, frame assembly/parsing, ACK/NAK handling,
timeouts, an on-board FIFO mirror, unsolicited `0x32` print-complete events and
automatic reconnect — behind a small async API.

```csharp
using DominoA200Sdk;
using DominoA200Sdk.Models;

using DominoA200Client printer = new DominoA200Client("192.168.1.50", 8001, autoReconnect: true);

printer.OnJobCompleted += (sender, args) =>
    Console.WriteLine($"printed {args.CodeValue} ({args.JobId})");

await printer.ConnectAsync();
string jobId = await printer.SendPrintJobAsync(new PrintJob("2026-09-17", "ABC000123"));
await printer.DisconnectAsync();
```

> ⚠️ **Not the official Domino SDK.** The protocol was obtained by reverse engineering
> captured network traffic; this library is an interoperability study published for
> learning and technical verification. It is not affiliated with, endorsed by or
> supported by Domino Printing Sciences plc, and must not be resold or shipped as a
> product. For production integration, obtain the vendor SDK.
>
> **本协议为网络抓包逆向分析所得，本项目仅用于学习与技术验证，不得用于商业用途。**
> Full statement: [../LICENSE](../LICENSE).

---

## Can this be used on its own?

**Yes.** The library has no dependencies of any kind:

| Project | Target | References | Role |
|---|---|---|---|
| **`DominoA200Sdk`** | `net8.0` | **nothing** — no `PackageReference`, no `ProjectReference` | the library |
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
    port:              8001,             // Codenet TCP port
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

## Constructor parameters

| Parameter | Default | Meaning |
|---|---|---|
| `host` | — | Printer host name or IP. Required, non-blank. |
| `port` | `8001` | Codenet TCP port. |
| `responseTimeoutMs` | `3000` | How long to wait for `0x06` / `0x15`. Also applied as the socket's send/receive timeout. |
| `connectTimeoutMs` | `5000` | TCP connect timeout. |
| `autoReconnect` | `false` | Background reconnect after a lost link. |
| `reconnectDelayMs` | `2000` | Delay between reconnect attempts. |

Values `<= 0` fall back to the defaults above.

---

## What throws, and what to do about it

| Exception | Raised when | Handling |
|---|---|---|
| `PrinterNackException` | The printer answers `0x15`. For a job this means the on-board FIFO (3 deep) is full. | Routine flow control: hold the job and retry shortly. |
| `PrinterTimeoutException` | No answer within `responseTimeoutMs`; also thrown when the write itself fails. Either way the SDK marks the link as lost first (auto-reconnect engages when enabled). | Check the cable/network, then reconnect. |
| `InvalidOperationException` | `SendPrintJobAsync` / `GetFifoQueueCountAsync` called while not connected. | Call `ConnectAsync()` first; with auto-reconnect, wait for `OnConnected`. |
| `SocketException`, `TimeoutException` | `ConnectAsync` could not reach the printer, or exceeded `connectTimeoutMs`. | Verify IP/port; a printer that reports "maximum connections reached" needs its old sockets to time out. |
| `ArgumentException` / `ArgumentNullException` | Bad input: blank host; null job; code value null/blank, longer than 9999 characters, or containing non-ASCII characters. | Validate at the edge, before submitting. |

---

## Job payload rules

`new PrintJob(dateText, codeValue)` renders `"<date> <code>"` (`ToPayload()`), or just
the code when the date is empty, and the SDK frames it as
`1B 4F 45 <4-digit length> <ASCII payload> 04`.

* the code value must be non-blank, at most **9999** characters, and **ASCII only** —
  the length field is a 4-digit decimal and the printer expands the text with its own
  template (layout, font and barcode encapsulation all live on the printer side);
* the SDK does **not** wrap the value in a barcode or QR envelope — that is the
  printer template's job.

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

---

## Requirements

* **.NET 8 SDK** to build, **.NET 8 runtime** to run (`Microsoft.NETCore.App` 8.x;
  the WinForms host additionally needs `Microsoft.WindowsDesktop.App` 8.x).
* No packages to restore — the library has no dependencies.
* The demo, mock and test projects are console/test apps targeting `net8.0`.

---

## Limits and known gaps

* **Unofficial protocol.** Every byte format here was derived from packet captures of
  one A200+ integration; other firmware revisions or printer configurations may differ.
* **Print head 1 only** — the signal-setup frame used by `ConnectAsync` addresses head 1.
* **The printer's true queue depth is never parsed**; the SDK reports its own mirror
  (see above).
* **TCP only** in this library. Serial transport exists in the WinForms host, not here.
* **No TLS** — the Codenet port is a plain TCP socket, so keep it on a trusted network.
* **No delivery guarantee.** A job that the printer accepted but never printed is only
  observable as a missing `0x32`; there is no re-print from the SDK.

---

## More documentation

| Document | Contents |
|---|---|
| [../Docs/ApiReference.md](../Docs/ApiReference.md) | Every public type, member and default. |
| [../Docs/ProtocolNotes.md](../Docs/ProtocolNotes.md) | Observed wire behaviour with byte-level examples. |
| [../README.md](../README.md) | Repository overview: mock, demo, tests, layout. |

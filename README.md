# Domino A200+ Codenet SDK

A .NET 8 client library for the Domino A200+ inkjet printer Codenet protocol, shipped
with a **mock simulator**, an **automated test harness**, and a **console demo** — so
the whole thing can be evaluated end to end without a physical printer.

```
┌──────────────┐   TCP 8001    ┌──────────────────┐
│   Your app   │ ────────────► │  A200+ printer   │
│  + this SDK  │ ◄──────────── │  (or the mock)   │
└──────────────┘   0x06/0x32   └──────────────────┘
```

---

## ⚠️ Important notice — read first

> **This is NOT the official Domino SDK.** It is not affiliated with, endorsed by, or
> supported by Domino Printing Sciences plc.
>
> **This demo is only for technical demonstration, not for commercial sale of
> Domino-related SDK.**

### Compliance statement / 合规说明

> **本协议是通过网络抓包（packet capture）逆向工程（reverse engineering）反向分析得到的，
> 本项目仅用于学习和技术验证，不得用于商业用途。**
>
> The protocol described and implemented here was obtained **solely by reverse
> engineering captured network traffic** (packet sniffing of our own integration
> environment). No vendor manual, SDK, firmware image or confidential documentation
> was consulted, reproduced or distributed.
>
> This project exists **for learning and technical verification only**: studying how a
> byte-level device protocol works, and validating that study in code. It must not be
> used for commercial purposes, nor relied upon for production integrations.

The code here is an independent interoperability study. It is published for
educational and portfolio purposes so that others can see how a byte-level device
protocol integration is structured and tested.

**If you need a production integration, obtain the official vendor SDK from the
manufacturer.** This repository grants no rights to any Domino intellectual property,
trademark or documentation — see [LICENSE](LICENSE) for the full statement.

---

## What's in the box

| Project | Type | Purpose |
|---|---|---|
| **`DominoA200Sdk`** | Class library | The client you reference. Hides TCP, framing, ACK/NAK handling, timeouts, FIFO mirroring and reconnect behind a small async API. |
| **`DominoMockServer`** | Console app | Simulates an A200+ over TCP. No printer required. Logs all traffic as hex. |
| **`DemoConsoleApp`** | Console app | A short end-to-end demonstration of the SDK against the mock. |
| **`DominoSdk.Harness`** | xUnit project | Automated tests that start the mock themselves and assert on protocol behaviour. |
| **`InkPrinterCode`** | WinForms app | The host application this SDK work originated from. |

---

## Quick start

### 1. Run the mock simulator

```bash
dotnet run --project DominoMockServer
```

```
==============================================================
  Domino A200+ Codenet Mock Simulator
--------------------------------------------------------------
Starting mock printer on 127.0.0.1:8001 ...
Emulated limits: FIFO capacity 3, print time 1500 ms.
```

### 2. Run the demo, in a second terminal

```bash
dotnet run --project DemoConsoleApp
```

You will see the connection handshake, two batches of three jobs submitted and
acknowledged, a deliberate queue overflow (a fourth job) rejected with `0x15`, and
the `0x32` completion events arriving asynchronously — with every byte printed as
raw hexadecimal:

```
    [08:14:22.310] [TX     ] 1B 49 31 32 04
    [08:14:22.318] [RX     ] 06
    [08:14:22.325] [TX     ] 1B 4F 45 30 30 30 37 41 42 43 30 30 30 30 30 31 04
    [08:14:22.334] [RX     ] 06
    ...
    [08:14:24.401] [RX     ] 32
[event] Job finished, JobId:JOB-000001  Code:2026-09-15 ABC000001
```

### 3. Run the test suite

```bash
dotnet test
```

The harness starts its own simulator on a free port, runs the cases, and shuts it
down — nothing to start manually.

---

## Using the SDK

Add a project or package reference to `DominoA200Sdk`, then:

```csharp
var printer = new DominoA200Client("127.0.0.1", 8001);
await printer.ConnectAsync();

// Subscribe before submitting, so a fast completion is not missed.
printer.OnJobCompleted += (sender, args) =>
{
    Console.WriteLine($"Job finished, JobId:{args.JobId}");
};

var job = new PrintJob("2026-09-15", "ABC123456");
string jobId = await printer.SendPrintJobAsync(job);

int pending = await printer.GetFifoQueueCountAsync();
Console.WriteLine($"Pending jobs in FIFO: {pending}");

await printer.DisconnectAsync();
```

### What the SDK handles for you

- **TCP packet sticking** — coalesced and split reads are reassembled into frames.
- **Frame encoding and framing** — headers, terminators and the 4-digit length field.
- **Response handling** — waits for `0x06`, with a configurable timeout.
- **Rejection handling** — a `0x15` becomes a `PrinterNackException`.
- **Unsolicited completion events** — `0x32` is watched for continuously, independent
  of any request, and surfaced through `OnJobCompleted`.
- **Disconnect detection and reconnect** — optional, configurable background recovery.

### Core types

| Type | Namespace | Role |
|---|---|---|
| `DominoA200Client` | `DominoA200Sdk` | The entry point. |
| `PrintJob` | `DominoA200Sdk.Models` | A job to print. |
| `PrinterStatus` | `DominoA200Sdk.Models` | A status snapshot. |
| `PrinterEventArgs` | `DominoA200Sdk.Models` | Completion-event payload. |
| `PrinterNackException` | `DominoA200Sdk.Exceptions` | Raised on `0x15`. |
| `PrinterTimeoutException` | `DominoA200Sdk.Exceptions` | Raised on no response. |
| `PrinterState` / `PrinterStateMachine` | `DominoA200Sdk.Core` | State tracking. |
| `CodenetFrame` / `JobQueue` | `DominoA200Sdk.Core` | Framing and FIFO mirror. |

Full detail: **[Docs/ApiReference.md](Docs/ApiReference.md)**

---

## How the protocol behaves

A short version of what was observed on the wire. Full notes, including byte-level
examples: **[Docs/ProtocolNotes.md](Docs/ProtocolNotes.md)**

| Response | Meaning |
|---|---|
| `0x06` | Command accepted. For a job, it is now queued. |
| `0x15` | Command refused. For a job, the queue is full. |
| `0x32` | A print finished — pushed unsolicited, with no job id attached. |

Three behaviours are worth calling out, because they are what makes this integration
non-trivial:

1. **The FIFO holds only three jobs.** The fourth submission is refused with `0x15`
   until one completes. Refusal is routine at production line rates, not an error.
2. **Completion events are unsolicited and unidentified.** A bare `0x32` arrives
   whenever a print finishes, carrying no job id, so it must be correlated against a
   locally maintained queue.
3. **The printer must be told to report completion.** The setup frame
   `1B 49 31 32 04` has to be sent first, or the printer prints silently and no
   completion event ever arrives.

---

## Repository layout

```
InkPrinterCode/                    ← repository root = solution root
├── README.md                      ← this file
├── LICENSE                        ← MIT, with the non-official-SDK notice
├── InkPrinterCode.slnx            ← the solution (5 projects)
├── Docs
│   ├── ApiReference.md            ← full API documentation
│   └── ProtocolNotes.md           ← observed wire behaviour and byte examples
├── InkPrinterCode/                ← WinForms host application
├── DominoA200Sdk/                 ← the client library
│   ├── Core
│   │   ├── CodenetFrame.cs
│   │   ├── PrinterStateMachine.cs
│   │   └── JobQueue.cs
│   ├── Models
│   │   ├── PrintJob.cs
│   │   ├── PrinterStatus.cs
│   │   └── PrinterEventArgs.cs
│   ├── Exceptions
│   │   ├── PrinterNackException.cs
│   │   └── PrinterTimeoutException.cs
│   └── DominoA200Client.cs
├── DominoMockServer/              ← TCP simulator
│   ├── MockPrinter.cs
│   ├── MockFifoQueue.cs
│   ├── CodenetHandler.cs
│   └── Program.cs
├── DemoConsoleApp/                ← demonstration
│   └── Program.cs
└── DominoSdk.Harness/             ← automated tests (xUnit)
    ├── TestFixture
    │   └── MockServerFixture.cs
    └── TestCases
        ├── ConnectTests.cs
        ├── SendJobTests.cs
        ├── JobCompleteEventTests.cs
        └── DisconnectReconnectTests.cs
```

---

## Test coverage

The harness is the part that distinguishes this from a hand-written demo: rather than
a human clicking buttons, the whole protocol path is asserted in code.

| Area | Cases |
|---|---|
| **Connect** | Session establishes and completes the handshake; handshake commands are actually issued; a dead port fails rather than hangs. |
| **Send job** | A job is acknowledged and queued; the fourth job is refused with `PrinterNackException`; empty code values are rejected client-side; payload formatting is correct. |
| **Completion events** | `OnJobCompleted` fires after the print delay with the correlated job id; the FIFO count drops as events are consumed. |
| **Disconnect / reconnect** | Disconnect raises its event once and updates state; repeated disconnect is idempotent; a fresh session works after disconnect and resets the mirror; the FIFO mirror reports capacity and clears correctly. |

```bash
dotnet test
```

---

## Requirements

- **.NET 8 SDK** (the library and demo target `net8.0`)
- No external dependencies; the SDK is dependency-free by design
- Tests require the xUnit runner packages, restored automatically

---

## A note on scope

This repository demonstrates technique: how to structure a device-protocol client, how
to simulate the device so it can be tested, and how to prove the integration works
automatically. It is deliberately published as a work sample rather than as a product.

For commercial deployment, use the manufacturer's official SDK, or implement against a
protocol you hold the necessary rights to.

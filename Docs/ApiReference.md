# API Reference

Reference for the public surface of `DominoA200Sdk`. The namespace is `DominoA200Sdk`,  
with supporting types under `DominoA200Sdk.Core`, `DominoA200Sdk.Models` and  
`DominoA200Sdk.Exceptions`.

---

## `DominoA200Client`

The TCP/IP client. Create one per printer endpoint and reuse it. For a serial link use  
`DominoA200SerialClient` instead (documented below); the two share the same API surface.

### Constructor

```csharp
public DominoA200Client(
    string host,
    int port = 7000,
    int responseTimeoutMs = 3000,
    int connectTimeoutMs = 5000,
    bool autoReconnect = false,
    int reconnectDelayMs = 2000)
```

| Parameter           | Default | Description                                                        |
| ------------------- | ------- | ------------------------------------------------------------------ |
| `host`              | —       | Printer host name or IP. Required; must not be null or whitespace. |
| `port`              | `7000`  | Codenet TCP port.                                                  |
| `responseTimeoutMs` | `3000`  | How long to wait for a response byte before declaring a timeout.   |
| `connectTimeoutMs`  | `5000`  | TCP connect timeout.                                               |
| `autoReconnect`     | `false` | When true, a lost link starts a background reconnect loop.         |
| `reconnectDelayMs`  | `2000`  | Delay between reconnect attempts.                                  |

Throws `ArgumentException` when `host` is null or empty.

### Properties

| Member          | Type              | Description                                   |
| --------------- | ----------------- | --------------------------------------------- |
| `IsConnected`   | `bool`            | Whether the transport is currently up.        |
| `State`         | `PrinterState`    | Current high-level state.                     |
| `Host` / `Port` | `string` / `int`  | The configured endpoint.                      |
| `TrafficLogger` | `Action<string>?` | Optional sink receiving every raw TX/RX line. |

### Methods

#### `Task ConnectAsync(CancellationToken cancellationToken = default)`

Opens the TCP connection, starts the receive loop, and performs the handshake:  
enable print-complete notification, then clear all three printer queues. Raises  
`OnConnected` once the transport is up.

Throws `SocketException` when the connection is refused, `TimeoutException` when the  
connect exceeds `connectTimeoutMs`, and `PrinterTimeoutException` if the printer does  
not answer a handshake command.

Calling it on an already-connected client returns immediately.

#### `Task<string> SendPrintJobAsync(PrintJob job, CancellationToken cancellationToken = default)`

Submits a job and waits for the printer's answer. On `0x06` the job is added to the  
client's FIFO mirror and a generated job identifier is returned.

| Exception                   | Raised when                                                                                                              |
| --------------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `ArgumentNullException`     | `job` is null.                                                                                                           |
| `ArgumentException`         | The payload is empty, longer than 9999 characters, non-ASCII, or contains `0x04`. Thrown **before** anything is written. |
| `InvalidOperationException` | The client is not connected.                                                                                             |
| `PrinterNackException`      | The printer refused the job with `0x15`.                                                                                 |
| `PrinterTimeoutException`   | No answer arrived within `responseTimeoutMs`.                                                                            |

#### `Task<int> GetFifoQueueCountAsync(CancellationToken cancellationToken = default)`

Returns the number of jobs awaiting completion. A read-only query, so a missing reply  
is tolerated and the locally mirrored count is returned.

Throws `InvalidOperationException` when not connected.

#### `PrinterStatus GetStatus()`

Returns a snapshot combining connection state, FIFO depth and the state label.

#### `Task DisconnectAsync()` / `void Dispose()`

Closes the connection, stops the receive loop and resets the FIFO mirror. Both are  
idempotent. `DisconnectAsync` raises `OnDisconnected` once.

### Events

| Event            | Payload                        | Raised on                     |
| ---------------- | ------------------------------ | ----------------------------- |
| `OnJobCompleted` | `PrinterEventArgs`             | receive thread                |
| `OnConnected`    | `EventArgs`                    | caller / reconnect thread     |
| `OnDisconnected` | `EventArgs`                    | receive or caller thread      |
| `OnStateChanged` | `PrinterStateChangedEventArgs` | thread causing the transition |
| `OnReconnecting` | `EventArgs`                    | reconnect thread              |

> `OnJobCompleted` fires on the background receive thread. Marshal to the UI thread  
> before touching UI objects.

---

## `DominoA200SerialClient`

The same client over an **RS232 serial link**. It exposes an identical method, property  
and event surface to `DominoA200Client` — only the constructor and the two endpoint  
properties differ, so moving a printer between TCP and serial is a constructor change.

```csharp
public DominoA200SerialClient(
    string portName,
    int baudRate = 9600,
    int responseTimeoutMs = 3000,
    int connectTimeoutMs = 5000,
    bool autoReconnect = false,
    int reconnectDelayMs = 2000)
```

| Parameter           | Default | Description                                                               |
| ------------------- | ------- | ------------------------------------------------------------------------- |
| `portName`          | —       | Serial port name (for example `COM3`). Required; must not be blank.       |
| `baudRate`          | `9600`  | RS232 baud rate. The link is always 8 data bits / 1 stop bit / no parity. |
| `responseTimeoutMs` | `3000`  | How long to wait for an ACK/NAK before declaring a timeout.               |
| `connectTimeoutMs`  | `5000`  | Accepted for signature parity only — `SerialPort.Open()` is synchronous.  |
| `autoReconnect`     | `false` | When true, a lost link starts a background reconnect loop.                |
| `reconnectDelayMs`  | `2000`  | Delay between reconnect attempts.                                         |

Throws `ArgumentException` when `portName` is null or empty.

### Additional properties

| Member     | Type     | Description                  |
| ---------- | -------- | ---------------------------- |
| `PortName` | `string` | Configured serial port name. |
| `BaudRate` | `int`    | Configured baud rate.        |

### How it differs from the TCP client

| Aspect                                                       | `DominoA200Client`                      | `DominoA200SerialClient`                      |
| ------------------------------------------------------------ | --------------------------------------- | --------------------------------------------- |
| Transport                                                    | TCP socket, port `7000` by default      | `SerialPort`, 8N1, `9600` baud by default     |
| Endpoint members                                             | `Host` / `Port`                         | `PortName` / `BaudRate`                       |
| Close                                                        | RST-forced (`Shutdown` + `LingerState`) | Closes and disposes the port                  |
| Connect failures                                             | `SocketException` / `TimeoutException`  | `UnauthorizedAccessException` / `IOException` |
| Framing, FIFO mirror, state machine, ACK/NAK/`0x32` handling | identical                               | identical                                     |

The `SerialPort` object is guarded by a single I/O lock, so the receive thread's read and  
the caller's write never touch it concurrently. A short read timeout keeps the receive  
loop responsive to `DisconnectAsync()`.

---

## `PrintJob` (`DominoA200Sdk.Models`)

An immutable job description.

```csharp
public PrintJob(string dateText, string codeValue)
```

| Member        | Description                                                         |
| ------------- | ------------------------------------------------------------------- |
| `DateText`    | The date field value, typically `yyyy-MM-dd`.                       |
| `CodeValue`   | The code text to print. Must not be null or whitespace.             |
| `ToPayload()` | Renders `"<date> <code>"`, or just the code when the date is empty. |

Throws `ArgumentException` when `codeValue` is null, empty or whitespace. The remaining  
payload rules (ASCII only, at most 9999 characters, no `0x04`) are enforced by  
`CodenetFrame.ValidatePrintJobPayload` when the job is submitted — see  
[`CodenetFrame`](#codenetframe-dominoa200sdkcore).

---

## `PrinterStatus` (`DominoA200Sdk.Models`)

| Member           | Type     | Description                 |
| ---------------- | -------- | --------------------------- |
| `IsConnected`    | `bool`   | Transport state.            |
| `FifoQueueCount` | `int`    | Jobs awaiting completion.   |
| `StateText`      | `string` | Human-readable state label. |

---

## `PrinterEventArgs` (`DominoA200Sdk.Models`)

| Member        | Type       | Description                                   |
| ------------- | ---------- | --------------------------------------------- |
| `JobId`       | `string`   | Correlated job id, or empty if unmatchable.   |
| `CodeValue`   | `string`   | Code text of the completed job, or empty.     |
| `CompletedAt` | `DateTime` | Local time the completion event was observed. |

---

## `PrinterState` (`DominoA200Sdk.Core`)

| Value          | Meaning                                            |
| -------------- | -------------------------------------------------- |
| `Disconnected` | No transport connection.                           |
| `Idle`         | Connected and ready to accept jobs.                |
| `Printing`     | A job is in progress.                              |
| `Alarm`        | The printer reported a fault or refused a command. |

---

## `PrinterStateMachine` (`DominoA200Sdk.Core`)

Tracks state and raises `StateChanged` on transitions. Transitions to the current  
state are no-ops. Thread-safe; the event is raised outside the internal lock.


```csharp
public PrinterState State { get; }
public event EventHandler<PrinterStateChangedEventArgs>? StateChanged;
public void TransitionTo(PrinterState target);
public void ReportAlarm();
```

---

## `PrinterStateChangedEventArgs` (`DominoA200Sdk.Core`)

Payload for `PrinterStateMachine.StateChanged`, surfaced to callers through  
`DominoA200Client.OnStateChanged`.

| Member     | Type           | Description                           |
| ---------- | -------------- | ------------------------------------- |
| `Previous` | `PrinterState` | The state held before the transition. |
| `Current`  | `PrinterState` | The state held after the transition.  |

---

## `JobQueue` (`DominoA200Sdk.Core`)

The FIFO mirror. Thread-safe.

| Member              | Description                                           |
| ------------------- | ----------------------------------------------------- |
| `Count`             | Jobs awaiting completion.                             |
| `IsFull`            | True at capacity (`CodenetFrame.FIFO_CAPACITY`).      |
| `Enqueue(id, code)` | Add a job, called after an ACK.                       |
| `DequeueOldest()`   | Remove and return the oldest job, or null when empty. |
| `Clear()`           | Discard everything; used on connect and disconnect.   |

---

## `CodenetFrame` (`DominoA200Sdk.Core`)

Static framing helpers.

### Constants

| Name            | Value  | Meaning                   |
| --------------- | ------ | ------------------------- |
| `ESC`           | `0x1B` | Frame header.             |
| `EOT`           | `0x04` | Frame terminator.         |
| `ACK`           | `0x06` | Positive acknowledgement. |
| `NAK`           | `0x15` | Negative acknowledgement. |
| `PRINT_DONE`    | `0x32` | Print-complete event.     |
| `FIFO_CAPACITY` | `3`    | On-board queue depth.     |

### Methods

| Method                               | Description                                                       |
| ------------------------------------ | ----------------------------------------------------------------- |
| `BuildSignalSetupFrame()`            | Enables print-complete notification for head 1.                   |
| `BuildPrintJobFrame(codeValue)`      | Builds a send-cached-data frame for a code text; validates first. |
| `ValidatePrintJobPayload(codeValue)` | Throws `ArgumentException` if the code cannot be framed.          |
| `BuildFifoQueryFrame()`              | Builds the FIFO depth query.                                      |
| `BuildClearQueueFrame(queueIndex)`   | Builds a queue-clear frame; index 0-2.                            |
| `TryParseFrame(buffer, out frame)`   | Extracts a complete terminated frame from a buffer.               |
| `ToHexString(data, length)`          | Renders bytes as spaced uppercase hex.                            |
| `ToHexByte(value)`                   | Renders one byte as two hex digits.                               |

#### `void ValidatePrintJobPayload(string codeValue)`

Verifies that `codeValue` can be carried inside an `OE` print-job frame, and throws if it  
cannot. Called by `BuildPrintJobFrame`, and by both transports *before* they write  
anything, so an invalid payload fails immediately as a caller error rather than costing an  
ACK round-trip.

| Rejected                | Reason                                                               |
| ----------------------- | -------------------------------------------------------------------- |
| `null` or `""`          | An `OE` frame with no code carries nothing to print.                 |
| Length `> 9999`         | The length field is four decimal digits.                             |
| Any character `> 0x7F`  | The printer template defines no other encoding.                      |
| Any character `== 0x04` | It is the frame terminator; it would truncate the frame mid-payload. |

All four raise `ArgumentException` naming the offending length or character. `0x1B` is  
**not** rejected — only the first byte of a frame is special.

#### `bool TryParseFrame(List<byte> buffer, out byte[] frameBytes)`

Extracts the first complete frame from `buffer` and removes nothing — the caller consumes  
`frameBytes.Length` bytes on success. Returns `false` when no complete frame is available  
yet.

For frames whose header is `1B 4F 45` the declared length is read from the four following  
bytes and the terminator must sit at `3 + 4 + declared`; a mismatch returns `false`  
(need more bytes) instead of consuming a frame whose payload cannot be trusted. Frames  
that are not length-prefixed — the FIFO query (`00017`), the clear-queue literal  
(`0000` + index) and the signal-setup frame — fall back to the first scanned `0x04`.

---

## Exceptions (`DominoA200Sdk.Exceptions`)

### `PrinterNackException`

Thrown when the printer refuses a command with `0x15`. For a print job the usual cause  
is a full on-board queue.

### `PrinterTimeoutException`

Thrown when no answer arrives within `responseTimeoutMs`. Distinct from a refusal: the  
command was written but never answered, which the client treats as a liveness failure.

---

## `DominoMockServer`

The simulator, for development and demonstration without hardware.

```csharp
var printer = new MockPrinter(port: 7000, printDurationMs: 1500, quiet: false);
printer.LogLine += (sender, line) => Console.WriteLine(line);
printer.Start();
// ...
printer.Stop();
```

| Member                      | Description                                                                                                                 |
| --------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| `Port`                      | The listening port.                                                                                                         |
| `DEFAULT_PRINT_DURATION_MS` | `const int`, default simulated print time (`1500` ms).                                                                      |
| `Silent`                    | When `true`, connections stay open but no command is ever answered — models a printer that is reachable yet not responding. |
| `LogLine`                   | Event raised for each logged traffic line.                                                                                  |
| `Start()`                   | Bind and begin accepting (non-blocking).                                                                                    |
| `Stop()`                    | Stop listening, drop all sessions, and wait for the accept thread to finish before returning.                               |

Emulated limits: FIFO capacity 3, print duration configurable (1500 ms by default),  
`0x15` on overflow, unsolicited `0x32` after each simulated print.

**One session at a time.** `AcceptLoop` serves a single client to completion before  
accepting the next, mirroring a physical printer with one Codenet port. A second client  
that connects while the first is still open is not rejected — it simply waits to be  
accepted, so a test that leaves a connection open will stall every later connection.

**Reachability vs. liveness.** `Stop()` closes the listening socket and every live  
session, which a client observes immediately as a lost link. To model the harder case —  
the printer answers the TCP handshake but has stopped replying — set `Silent = true`  
instead: the socket stays open and frames are absorbed without a reply, so the caller  
hits its own command timeout rather than a disconnect.

`MockPrinter` measures incoming frames the same way the client parses them: an `OE` frame  
is bounded by its declared length (the terminator must sit at `3 + 4 + declared`), while  
the fixed-layout commands fall back to the first `0x04`. The mock therefore rejects the  
same malformed frames a real printer would, instead of silently accepting a lying length  
field.

`MockFifoQueue` and `CodenetHandler` are public so the emulated admission rules and  
command grammar can be tested in isolation, without opening a socket.

---

## `CodenetHandler` (`DominoMockServer`)

Command-level façade over the mock printer's protocol handling. Exposed so the  
emulated command grammar can be unit-tested without a socket.

### Members

| Member                | Description                                                           |
| --------------------- | --------------------------------------------------------------------- |
| `Parse(byte[] frame)` | Interprets a raw frame, returning a `ParsedCommand`.                  |
| `Log`                 | Optional sink (`Action<string>?`) for malformed-frame diagnostics.    |
| `CommandKind` (enum)  | `Unknown`, `SignalSetup`, `FifoQuery`, `ClearQueue`, `PrintJob`.      |
| `ParsedCommand`       | Parse result exposing `Kind`, `Payload` (print job) and `QueueIndex`. |

For a print job the four length digits must all be decimal **and** must match the payload  
length exactly; a frame that disagrees is rejected as malformed rather than accepted with  
an incorrect code, so the mock and the real printer fail the same inputs.

---

## `MockFifoQueue` (`DominoMockServer`)

Simulates the A200+ on-board FIFO. Exposed so the three-deep admission rule can be  
tested in isolation.

| Member                | Description                                                              |
| --------------------- | ------------------------------------------------------------------------ |
| `CAPACITY`            | `const int`, fixed at `3` — mirrors the hardware's three-deep queue.     |
| `Count`               | Current number of held jobs.                                             |
| `IsFull`              | `true` when `Count >= CAPACITY`.                                         |
| `TryEnqueue(MockJob)` | Admit a job; returns `false` (caller should NAK) when the queue is full. |
| `DequeueOldest()`     | Remove and return the oldest job, or `null` when empty.                  |
| `Clear()`             | Discard all queued jobs.                                                 |

---

## `MockJob` (`DominoMockServer`)

A job held by the mock FIFO, standing in for a real print job.

| Member                                  | Type        | Description                       |
| --------------------------------------- | ----------- | --------------------------------- |
| `MockJob(string jobId, string payload)` | constructor | Creates a mock job.               |
| `JobId`                                 | `string`    | Identifier assigned by the mock.  |
| `Payload`                               | `string`    | The code text carried by the job. |

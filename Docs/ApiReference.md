# API Reference

Reference for the public surface of `DominoA200Sdk`. The namespace is `DominoA200Sdk`,
with supporting types under `DominoA200Sdk.Core`, `DominoA200Sdk.Models` and
`DominoA200Sdk.Exceptions`.

---

## `DominoA200Client`

The single entry point. Create one per printer endpoint and reuse it.

### Constructor

```csharp
public DominoA200Client(
    string host,
    int port = 8001,
    int responseTimeoutMs = 3000,
    int connectTimeoutMs = 5000,
    bool autoReconnect = false,
    int reconnectDelayMs = 2000)
```

| Parameter            | Default | Description                                                          |
|----------------------|---------|----------------------------------------------------------------------|
| `host`               | —       | Printer host name or IP. Required; must not be null or whitespace.    |
| `port`               | `8001`  | Codenet TCP port.                                                    |
| `responseTimeoutMs`  | `3000`  | How long to wait for a response byte before declaring a timeout.      |
| `connectTimeoutMs`   | `5000`  | TCP connect timeout.                                                  |
| `autoReconnect`      | `false` | When true, a lost link starts a background reconnect loop.            |
| `reconnectDelayMs`   | `2000`  | Delay between reconnect attempts.                                     |

Throws `ArgumentException` when `host` is null or empty.

### Properties

| Member                    | Type                 | Description                                              |
|---------------------------|----------------------|----------------------------------------------------------|
| `IsConnected`             | `bool`               | Whether the transport is currently up.                   |
| `State`                   | `PrinterState`       | Current high-level state.                                |
| `Host` / `Port`           | `string` / `int`     | The configured endpoint.                                 |
| `TrafficLogger`           | `Action<string>?`    | Optional sink receiving every raw TX/RX line.            |

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

| Exception                  | Raised when                                        |
|----------------------------|----------------------------------------------------|
| `ArgumentNullException`    | `job` is null.                                     |
| `InvalidOperationException`| The client is not connected.                       |
| `PrinterNackException`     | The printer refused the job with `0x15`.            |
| `PrinterTimeoutException`  | No answer arrived within `responseTimeoutMs`.      |

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

| Event              | Payload                          | Raised on                       |
|--------------------|----------------------------------|---------------------------------|
| `OnJobCompleted`   | `PrinterEventArgs`               | receive thread                  |
| `OnConnected`      | `EventArgs`                      | caller / reconnect thread       |
| `OnDisconnected`   | `EventArgs`                      | receive or caller thread        |
| `OnStateChanged`   | `PrinterStateChangedEventArgs`   | thread causing the transition   |
| `OnReconnecting`   | `EventArgs`                      | reconnect thread                |

> `OnJobCompleted` fires on the background receive thread. Marshal to the UI thread
> before touching UI objects.

---

## `PrintJob` (`DominoA200Sdk.Models`)

An immutable job description.

```csharp
public PrintJob(string dateText, string codeValue)
```

| Member        | Description                                                       |
|---------------|-------------------------------------------------------------------|
| `DateText`    | The date field value, typically `yyyy-MM-dd`.                      |
| `CodeValue`   | The code text to print. Must not be null or whitespace.            |
| `ToPayload()` | Renders `"<date> <code>"`, or just the code when the date is empty.|

Throws `ArgumentException` when `codeValue` is null, empty or whitespace.

---

## `PrinterStatus` (`DominoA200Sdk.Models`)

| Member           | Type     | Description                       |
|------------------|----------|-----------------------------------|
| `IsConnected`    | `bool`   | Transport state.                  |
| `FifoQueueCount` | `int`    | Jobs awaiting completion.         |
| `StateText`      | `string` | Human-readable state label.       |

---

## `PrinterEventArgs` (`DominoA200Sdk.Models`)

| Member        | Type       | Description                                             |
|---------------|------------|---------------------------------------------------------|
| `JobId`       | `string`   | Correlated job id, or empty if unmatchable.              |
| `CodeValue`   | `string`   | Code text of the completed job, or empty.                |
| `CompletedAt` | `DateTime` | Local time the completion event was observed.            |

---

## `PrinterState` (`DominoA200Sdk.Core`)

| Value          | Meaning                                              |
|----------------|------------------------------------------------------|
| `Disconnected` | No transport connection.                             |
| `Idle`         | Connected and ready to accept jobs.                  |
| `Printing`     | A job is in progress.                                |
| `Alarm`        | The printer reported a fault or refused a command.   |

---

## `PrinterStateMachine` (`DominoA200Sdk.Core`)

Tracks state and raises `StateChanged` on transitions. Transitions to the current
state are no-ops. Thread-safe; the event is raised outside the internal lock.

```csharp
public PrinterState State { get; }
public bool IsIdle { get; }
public event EventHandler<PrinterStateChangedEventArgs>? StateChanged;
public void TransitionTo(PrinterState target);
public void ReportAlarm();
```

---

## `JobQueue` (`DominoA200Sdk.Core`)

The FIFO mirror. Thread-safe.

| Member                       | Description                                                     |
|------------------------------|-----------------------------------------------------------------|
| `Count`                      | Jobs awaiting completion.                                       |
| `IsFull`                     | True at capacity (`CodenetFrame.FIFO_CAPACITY`).                |
| `Enqueue(id, code)`          | Add a job, called after an ACK.                                 |
| `Complete(jobId)`            | Remove a specific job, returning its entry or null.             |
| `DequeueOldest()`            | Remove and return the oldest job, or null when empty.           |
| `Clear()`                    | Discard everything; used on connect and disconnect.             |
| `SnapshotIds()`              | Ordered snapshot of queued ids, for diagnostics.                |

---

## `CodenetFrame` (`DominoA200Sdk.Core`)

Static framing helpers.

### Constants

| Name              | Value | Meaning                    |
|-------------------|-------|----------------------------|
| `ESC`             | `0x1B`| Frame header.              |
| `EOT`             | `0x04`| Frame terminator.          |
| `ACK`             | `0x06`| Positive acknowledgement.  |
| `NAK`             | `0x15`| Negative acknowledgement.  |
| `PRINT_DONE`      | `0x32`| Print-complete event.      |
| `FIFO_CAPACITY`   | `3`   | On-board queue depth.      |

### Methods

| Method                              | Description                                          |
|-------------------------------------|------------------------------------------------------|
| `BuildSignalSetupFrame()`           | Enables print-complete notification for head 1.      |
| `BuildPrintJobFrame(codeValue)`     | Builds a send-cached-data frame for a code text.     |
| `BuildFifoQueryFrame()`             | Builds the FIFO depth query.                         |
| `BuildClearQueueFrame(queueIndex)`  | Builds a queue-clear frame; index 0-2.               |
| `TryParseFrame(buffer, out frame)`  | Extracts a complete terminated frame from a buffer.  |
| `ToHexString(data, length)`         | Renders bytes as spaced uppercase hex.               |
| `ToHexByte(value)`                  | Renders one byte as two hex digits.                  |

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
var printer = new MockPrinter(port: 8001, printDurationMs: 1500, quiet: false);
printer.LogLine += (sender, line) => Console.WriteLine(line);
printer.Start();
// ...
printer.Stop();
```

| Member            | Description                                                |
|-------------------|------------------------------------------------------------|
| `Port`            | The listening port.                                        |
| `FifoCount`       | Current emulated queue depth.                              |
| `LogLine`         | Event raised for each logged traffic line.                 |
| `Start()`         | Bind and begin accepting (non-blocking).                   |
| `Stop()`          | Stop listening and drop all sessions.                      |

Emulated limits: FIFO capacity 3, print duration configurable (1500 ms by default),
`0x15` on overflow, unsolicited `0x32` after each simulated print.

`MockFifoQueue` and `CodenetHandler` are public so the emulated admission rules and
command grammar can be tested in isolation, without opening a socket.

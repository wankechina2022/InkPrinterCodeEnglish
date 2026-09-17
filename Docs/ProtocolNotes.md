# Protocol Notes

Observations on the wire behaviour of the Domino A200+ Codenet interface, recorded
from captured network traffic while integrating with the device.

> **Scope and compliance of this document.** The byte values and behaviours described
> here were obtained **by reverse engineering captured network traffic** (packet
> capture in our own integration environment) — no vendor manual or official
> documentation was consulted. This material is published **for learning and
> technical verification only**. These notes describe only what was *observed on the
> wire*. They deliberately do not reproduce, transcribe or paraphrase any vendor
> manual, and byte values listed here were read off a network capture rather than
> copied from documentation. Where behaviour was inferred rather than observed, it is
> marked as such.

---

## 1. Frame shapes

The protocol is a byte-oriented command/response protocol layered directly on TCP.
Two frame families were observed.

### 1.1 Generic command frame

```
1B  <command bytes>  04
│                     └── EOT terminator
└── ESC header
```

The frame starts with `0x1B` (ESC) and ends with `0x04` (EOT). Everything between is
command-specific.

A fixed example observed during setup:

```
1B 49 31 32 04          ESC 'I' '1' '2' EOT
```

### 1.2 `OE` family frame

```
1B 4F 45  <len:4 ASCII digits>  <payload bytes>  04
│  │  │
│  └──┴── 'O' 'E'
└── ESC
```

The four bytes immediately after `OE` are a **decimal, zero-padded, 4-digit length**
of the payload that follows. For example, sending the code text `AB12345` (7
characters) produces:

```
1B 4F 45 30 30 30 37 41 42 31 32 33 34 35 04
         └─ "0007" ─┘ └──── "AB12345" ─────┘
```

Everything in the payload is ASCII; code text is sent as bare characters with no
barcode or QR encapsulation. Enclosing a code in a symbology is the printer template's
responsibility, not the protocol's.

#### Observed `OE` payloads

| Payload (ASCII)       | Command           | Notes                                                                 |
|-----------------------|-------------------|-----------------------------------------------------------------------|
| `00017`               | FIFO depth query  | Sent when the caller asks for the queue depth.                        |
| `0000` + `index`      | Clear cache queue | `index` is `0` (TCP/IP), `1` (RS232) or `2` (history).                 |
| 4 digits + code text  | Send cached data  | The 4 digits are the zero-padded decimal length of the code text.      |

The leading four bytes are **not** a uniform length field. For `send cached data` they
are exactly the length of what follows, but for the depth query and the queue clear the
digits are command-specific: `00017` is a fixed literal, and `0000` is followed by a
queue index rather than by zero-length data. A parser therefore has to recognise those
two commands by their literal payload, not by decoding a length — which is what the
simulator in this repository does.

---

## 2. Response bytes

The printer answers a command with a single control byte. There is no correlation
field, no command echo, and no sequence number.

| Byte   | Name | Meaning observed                                             |
|--------|------|--------------------------------------------------------------|
| `0x06` | ACK  | Command accepted. For a print job, this means it was queued.  |
| `0x15` | NAK  | Command refused. For a print job, the queue was full.         |
| `0x32` | —    | Print-complete event, pushed unsolicited (see §4).            |

The answer to a command is the **first** control byte that follows it. This is why
the SDK serialises command dispatch: with no correlation field, two commands in flight
at once would make the responses ambiguous.

---

## 3. FIFO queue behaviour

The A200+ holds a small on-board queue of pending print jobs. The observed limit is
**three jobs**.

Admission rule, as observed:

```
if queue_depth < 3:
    accept the job
    reply 0x06
    queue_depth += 1
else:
    refuse the job
    reply 0x15
    queue_depth unchanged
```

Consequences worth designing around:

1. **`0x06` means "queued", not "printed".** A successful acknowledgement says only
   that the printer took ownership of the job. The actual marking happens later and is
   reported separately (§4).
2. **Refusal is normal operating behaviour, not an error condition.** At line rates
   above roughly three jobs per print cycle, `0x15` will be seen routinely. A caller
   that treats a NAK as a hard failure will be unreliable in production.
3. **The client should mirror the queue.** Because the printer does not expose a
   reliable depth read-back over the commands observed here, the SDK tracks its own
   count: increment on `0x06` for a job, decrement on each `0x32` consumed, reset on
   connect and disconnect.

### Getting the printer to report completion

Printing the code text is not by itself enough for the printer to report back. The
print-complete notification must be **enabled first**, by sending:

```
1B 49 31 32 04
```

This configures head 1 to emit `0x32` when a print finishes. Without this setup step
the printer stays silent after printing, and the completion callback never fires. In
the SDK this frame is sent as part of the connection handshake, and again after every
reconnect.

---

## 4. Unsolicited print-complete events

When a job finishes printing, the printer sends a **single byte**:

```
32
```

with no frame header, no job identifier and no payload.

Two implications:

1. **The event must be correlated client-side.** Since the printer echoes no job id,
   the SDK associates each `0x32` with the oldest job in its own FIFO mirror and
   reports that job's identifier to the callback. This is a FIFO assumption, and it
   holds while jobs complete in submission order.
2. **Arrival is asynchronous and unpredictable.** The byte can land at any moment,
   including in the middle of an unrelated read, or immediately after a response byte
   in the same TCP segment. It must not be confused with an ACK.

Because a bare `0x32` carries no framing, a reader that assumes "every read is a
response to my last command" will misparse the stream. The receive path has to
interpret bytes by *shape*, not by *order*.

---

## 5. Packet sticking (coalesced and split reads)

TCP is a byte stream, not a message stream. Two failure modes were observed in
practice and both must be handled.

### 5.1 Multiple logical units in one read

A single `Read` can return an event byte immediately followed by the start of a frame:

```
32 1B 4F 45 ...
│  └── beginning of the next frame
└── print-complete event
```

A naive reader that treats the whole buffer as one message will lose both.

### 5.2 A frame split across reads

A frame can also arrive in fragments, with the terminator arriving in a later read:

```
read 1:  1B 4F 45 30 30 30 37 41
read 2:  42 31 32 33 34 35 04
```

The reader must therefore buffer partial frames and only dispatch once `0x04` has
been seen.

### 5.3 How the SDK resolves this

The receive buffer is drained one logical unit at a time:

- `0x06` → consume 1 byte, resolve the pending acknowledgement as success.
- `0x15` → consume 1 byte, resolve the pending acknowledgement as refusal.
- `0x32` → consume 1 byte, raise the print-complete event.
- `0x1B` → accumulate until the next `0x04`, then dispatch the complete frame; if no
  terminator is present yet, return and wait for more bytes.
- anything else → consume 1 byte, log it as an unrecognised byte and continue.

The order of these checks matters. The single-byte events are tested first precisely
because they can appear immediately in front of a frame header.

---

## 6. Connection lifecycle

Sequence the SDK performs on connect:

```
1. TCP connect to host:port
2. send  1B 49 31 32 04                 enable print-complete notification  → expect 0x06
3. send  1B 4F 45 30 30 30 30 30 04     clear queue index 0 (TCP/IP)        → expect 0x06
4. send  1B 4F 45 30 30 30 30 31 04     clear queue index 1 (RS232)         → expect 0x06
5. send  1B 4F 45 30 30 30 30 32 04     clear queue index 2 (history)       → expect 0x06
6. ready to submit jobs
```

The three queue clears target the TCP/IP, RS232 and history queues respectively. They
are issued so the mirrored FIFO count starts from a known-empty state rather than
inheriting whatever the printer was holding from a previous session.

On reconnect, the same sequence is repeated. Any job the client had not seen complete
must be treated as unknown, since the printer's queue state is not recoverable after a
link loss.

---

## 7. Timeouts and liveness

The commands observed here are answered in milliseconds on a healthy link. A response
that never arrives is therefore a meaningful signal, distinct from a refusal:

| Situation                        | Observed meaning                                  |
|----------------------------------|---------------------------------------------------|
| `0x06` received                  | command accepted                                  |
| `0x15` received                  | command refused (queue full / malformed)          |
| no response within the window     | the peer is not answering — a liveness failure    |

The SDK treats a timeout as distinct from a NAK: a NAK is a successful exchange with a
negative answer, whereas a timeout means the conversation itself broke down. Repeated
timeouts are what trigger the reconnect path.

Note that on TCP a half-open connection can still *accept* writes successfully while
nothing is reachable at the other end. Only a successful *read* proves the peer is
alive. Liveness must therefore be judged on inbound traffic, never on the mere fact
that a write did not throw.

---

## 8. Summary of constants

| Constant           | Value  | Meaning                                     |
|--------------------|--------|---------------------------------------------|
| Frame header       | `0x1B` | ESC, starts a frame                         |
| Frame terminator   | `0x04` | EOT, ends a frame                           |
| ACK                | `0x06` | command accepted                            |
| NAK                | `0x15` | command refused                             |
| Print complete     | `0x32` | unsolicited job-finished event              |
| FIFO capacity      | 3      | maximum queued jobs observed                |
| Length field width | 4      | decimal, zero-padded, within an `OE` frame  |

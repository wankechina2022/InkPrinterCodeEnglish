using System.Text;

namespace DominoA200Sdk.Core;

/// <summary>
/// Codenet framing helper: builds outgoing command frames and parses incoming
/// byte streams into discrete frames.
///
/// <para>
/// <b>Responsibility boundary.</b> This class does two things only:
/// (1) assemble command / code payloads into byte frames, and
/// (2) split a received byte stream back into frames.
/// It holds no connection, performs no I/O and keeps no state, so it is safe to
/// call from any thread.
/// </para>
///
/// <para>
/// <b>Observed wire behaviour.</b> The frame format below was derived from
/// captured network traffic (see <c>Docs/ProtocolNotes.md</c>), not from any
/// vendor manual:
/// </para>
/// <list type="bullet">
///   <item><description>Generic command frame: <c>1B ...payload... 04</c> (ESC ... EOT).</description></item>
///   <item><description><c>OE</c> family frame: <c>1B 4F 45 &lt;len&gt; &lt;data&gt; 04</c>, where
///   <c>&lt;len&gt;</c> is a 4-digit zero-padded decimal length.</description></item>
///   <item><description>ACK stream: <c>0x06</c> on success; <c>0x15</c>-prefixed on rejection.</description></item>
///   <item><description>Print-complete event: single byte <c>0x32</c>.</description></item>
///   <item><description>FIFO queue depth is capped at 3 jobs on the A200+.</description></item>
/// </list>
/// </summary>
public static class CodenetFrame
{
    // ============================================================
    // Protocol constants
    // ============================================================

    /// <summary>Frame header: <c>0x1B</c> (ESC).</summary>
    public const byte ESC = 0x1B;

    /// <summary>Frame terminator: <c>0x04</c> (EOT).</summary>
    public const byte EOT = 0x04;

    /// <summary>Positive acknowledgement: <c>0x06</c> (ACK).</summary>
    public const byte ACK = 0x06;

    /// <summary>Negative acknowledgement: <c>0x15</c> (NAK).</summary>
    public const byte NAK = 0x15;

    /// <summary>Print-complete event: the single byte <c>0x32</c> pushed by the printer.</summary>
    public const byte PRINT_DONE = 0x32;

    /// <summary>Maximum number of jobs the A200+ FIFO queue accepts before returning NAK.</summary>
    public const int FIFO_CAPACITY = 3;

    // ============================================================
    // Framing (outbound direction)
    // ============================================================

    /// <summary>
    /// Build the "print signal setup" frame: <c>1B 49 31 32 04</c> (ESC 'I' '1' '2' EOT).
    ///
    /// <para>
    /// <b>Why this matters.</b> It configures head 1 to emit <c>0x32</c> once printing
    /// finishes. Without it the printer never pushes the print-complete event and the
    /// completion callback can never fire.
    /// </para>
    /// </summary>
    public static byte[] BuildSignalSetupFrame()
    {
        return new byte[] { 0x1B, 0x49, 0x31, 0x32, 0x04 };
    }

    /// <summary>
    /// Build the "send cached data" frame: <c>1B 4F 45 &lt;len&gt; &lt;code ASCII&gt; 04</c>.
    ///
    /// <para>
    /// Example: sending <c>AB12345</c> yields
    /// <c>1B 4F 45 30 30 30 37 41 42 31 32 33 33 34 35 04</c> (length 7 encoded as "0007").
    /// </para>
    /// </summary>
    /// <param name="codeValue">The code text to print. Only the bare text is sent; no
    /// barcode or QR wrapper is applied.</param>
    public static byte[] BuildPrintJobFrame(string codeValue)
    {
        // [2026-09-16] Validate the payload before framing (F5). The length field is a
        // 4-digit zero-padded decimal, so it can encode at most 9999 characters; a
        // null/empty payload, an over-length one, or any non-ASCII character would
        // produce a frame the printer cannot parse, so reject it up front.
        if (string.IsNullOrEmpty(codeValue))
        {
            throw new ArgumentException("Print job payload must not be null or empty.", nameof(codeValue));
        }

        if (codeValue.Length > 9999)
        {
            throw new ArgumentException(
                "Print job payload is too long (" + codeValue.Length.ToString()
                + " chars); the 4-digit length field supports at most 9999.", nameof(codeValue));
        }

        foreach (char c in codeValue)
        {
            if (c > 127)
            {
                throw new ArgumentException(
                    "Print job payload contains a non-ASCII character (0x" + ((int)c).ToString("X2")
                    + "); only ASCII text is supported.", nameof(codeValue));
            }
        }

        // 4-digit zero-padded decimal length of the payload that follows it.
        string lengthText = codeValue.Length.ToString("D4");
        string payload = lengthText + codeValue;

        return BuildOeFrame(payload);
    }

    /// <summary>
    /// Build the "query FIFO queue depth" frame: <c>1B 4F 45 30 30 30 31 37 04</c>
    /// (OE with payload "00017"), used to ask the printer how many jobs are queued.
    /// </summary>
    public static byte[] BuildFifoQueryFrame()
    {
        return BuildOeFrame("00017");
    }

    /// <summary>
    /// Build the "clear cache queue" frame: <c>1B 4F 45 30 30 30 35 &lt;queue&gt; 04</c>.
    /// </summary>
    /// <param name="queueIndex">0 = TCP/IP queue, 1 = RS232 queue, 2 = history queue.
    /// Out-of-range values fall back to 0.</param>
    public static byte[] BuildClearQueueFrame(int queueIndex)
    {
        if (queueIndex < 0 || queueIndex > 2)
        {
            queueIndex = 0;
        }

        string payload = "0000" + queueIndex.ToString();
        return BuildOeFrame(payload);
    }

    /// <summary>
    /// Build the shared skeleton for OE-family frames: <c>1B 4F 45 &lt;payload ASCII&gt; 04</c>.
    /// </summary>
    private static byte[] BuildOeFrame(string payload)
    {
        byte[] payloadBytes = Encoding.ASCII.GetBytes(payload);

        byte[] frame = new byte[3 + payloadBytes.Length + 1];
        frame[0] = ESC;
        frame[1] = 0x4F;    // 'O'
        frame[2] = 0x45;    // 'E'
        Array.Copy(payloadBytes, 0, frame, 3, payloadBytes.Length);
        frame[frame.Length - 1] = EOT;

        return frame;
    }

    // ============================================================
    // Parsing (inbound direction)
    // ============================================================

    /// <summary>
    /// Attempt to interpret the head of <paramref name="buffer"/> as a complete
    /// <c>1B ... 04</c> frame.
    ///
    /// <para>
    /// <b>Calling convention.</b> The caller must only invoke this when the first
    /// buffered byte is <see cref="ESC"/>. If no complete frame is present the method
    /// returns <c>false</c> and the caller should wait for more bytes.
    /// </para>
    /// </summary>
    /// <param name="buffer">The receive buffer. Contents are not modified; the caller
    /// decides how many bytes to consume.</param>
    /// <param name="frameBytes">Output: the full frame including the terminator, or an
    /// empty array when no complete frame is available.</param>
    /// <returns><c>true</c> when a complete terminated frame was found.</returns>
    public static bool TryParseFrame(List<byte> buffer, out byte[] frameBytes)
    {
        frameBytes = Array.Empty<byte>();

        if (buffer == null || buffer.Count < 2 || buffer[0] != ESC)
        {
            return false;
        }

        // Scan forward for the first EOT terminator.
        int endIndex = -1;
        for (int i = 1; i < buffer.Count; i++)
        {
            if (buffer[i] == EOT)
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex < 0)
        {
            return false;
        }

        frameBytes = new byte[endIndex + 1];
        buffer.CopyTo(0, frameBytes, 0, endIndex + 1);
        return true;
    }

    // ============================================================
    // Logging helpers
    // ============================================================

    /// <summary>
    /// Render a byte array as uppercase, space-separated hexadecimal text.
    /// Example: <c>{0x1B, 0x0D}</c> becomes <c>"1B 0D"</c>.
    /// </summary>
    public static string ToHexString(byte[]? data, int length)
    {
        if (data == null || length <= 0)
        {
            return string.Empty;
        }

        if (length > data.Length)
        {
            length = data.Length;
        }

        StringBuilder builder = new StringBuilder(length * 3);

        for (int i = 0; i < length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }
            builder.Append(data[i].ToString("X2"));
        }

        return builder.ToString();
    }

    /// <summary>Render a single byte as two uppercase hexadecimal digits.</summary>
    public static string ToHexByte(byte value)
    {
        return value.ToString("X2");
    }
}

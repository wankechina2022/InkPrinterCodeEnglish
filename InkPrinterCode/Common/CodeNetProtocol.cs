using System.Text;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] CodeNet protocol framing and reply parsing (extracted into its own file;
    /// kept separate from the connection and scheduling layers)
    ///
    /// [Responsibility boundary (what this class does and does not do)]
    ///   This class does exactly two things: (1) assemble commands/code values into byte frames; (2) parse the
    ///   received byte stream into frames. It holds no connection, writes no database, and does no scheduling ——
    ///   the connection lives in TcpPrinterConnection / SerialPrinterConnection, and the scheduling state machine
    ///   lives in PrintServiceBLL.
    ///
    /// [Protocol basis] "1-A Series CodeNet Common Command Introduction.pdf" (TSG 2016.10) —— the command flow and
    ///   the reply determination are always governed by the PDF; the reference code DominoPrinter.cs only verified the
    ///   framing format (see section 1.8 of the design document for the byte-by-byte comparison).
    ///
    /// [Empirical framing conclusion (design v4)] The length field is fixed at 4 decimal digits with leading zeros
    ///   (per the PDF) —— this is byte-identical to the reference code for codes of >=10 digits (the main range on
    ///   site), while for short codes it avoids the misalignment risk of the reference code's "007".
    ///
    /// [Byte characteristics of the three feedback streams (the basis for parse routing; query
    /// commands must never be confused with print commands)]
    ///   ACK stream        : single byte 0x06 (success) / starting with 0x15 (rejected) —— only logged, never used for
    ///                       state determination (code-claim occupancy model)
    ///   Print-done stream : single byte 0x32 —— triggers code sending
    ///   Status frame stream: 1B 31 43 SSS B HHMM 04 (12 bytes) —— heartbeat liveness check + status code
    ///   Count frame stream : 1B 54 31 + 10 digits + 04 (14 bytes) —— reserved for reconciliation
    /// </summary>
    public static class CodeNetProtocol
    {
        // ============================================================
        // Protocol constants
        // ============================================================

        /// <summary>Frame header 0x1B (ESC)</summary>
        public const byte ESC = 0x1B;

        /// <summary>Frame trailer 0x04 (EOT)</summary>
        public const byte EOT = 0x04;

        /// <summary>Reply: received successfully 0x06 (ACK)</summary>
        public const byte ACK = 0x06;

        /// <summary>Reply: rejected / format error, starting with 0x15 (NAK)</summary>
        public const byte NAK = 0x15;

        /// <summary>Print-done event: single byte 0x32 (agreed by the "print signal setup" command)</summary>
        public const byte PRINT_DONE = 0x32;

        /// <summary>Status frame length: 1B 31 43 SSS B HHMM 04 = 12 bytes</summary>
        public const int STATUS_FRAME_LENGTH = 12;

        /// <summary>Count frame length: 1B 54 31 + 10 digits + 04 = 14 bytes</summary>
        public const int COUNT_FRAME_LENGTH = 14;

        // ============================================================
        // Framing (send direction)
        // ============================================================

        /// <summary>
        /// Build the "print signal setup" frame: 1B 49 31 32 04 (ESC 'I' '1' '2' EOT)
        /// [Purpose] Sets printhead 1 to send 0x32 back after printing is complete —— without this command the inkjet
        ///   printer never pushes 0x32, and the code-sending mechanism cannot work (the reference code is missing
        ///   exactly this command; see design section 1.8).
        /// </summary>
        public static byte[] BuildSignalSetupFrame()
        {
            return new byte[] { 0x1B, 0x49, 0x31, 0x32, 0x04 };
        }

        /// <summary>
        /// Build the "clear cache queue" frame: 1B 4F 45 + "0000" + queue index + 04
        /// [Queue index] 0 = TCP/IP queue; 1 = RS232 queue; 2 = history queue (PDF 3.1; all three must be cleared).
        /// </summary>
        /// <param name="queueIndex">Queue index 0/1/2; out-of-range is treated as 0</param>
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
        /// Build the "send cache data" frame: 1B 4F 45 + 4-digit zero-padded length + code value ASCII + 04
        /// [Example] Sending AB12345 → 1B 4F 45 30303037 41423132333435 04 (length 7 → "0007").
        /// [Precondition] The inkjet printer must already have a dynamic text template configured, otherwise the
        /// command is ineffective (it replies with 0x15).
        /// </summary>
        /// <param name="codeValue">Code value (text only; no barcode/QR code wrapping is performed)</param>
        public static byte[] BuildCacheDataFrame(string codeValue)
        {
            string text = codeValue ?? string.Empty;

            // [2026-09-17] Reject the frame terminator inside the payload (F10). The
            // receive side finds the end of a frame by scanning for the FIRST 0x04, so a
            // payload byte of 0x04 makes the frame look terminated early: the tail of the
            // code value is silently dropped and the rest is re-scanned as stray bytes.
            // Reject it here rather than putting a truncated code on the production line.
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == (char)EOT)
                {
                    throw new ArgumentException(
                        "Code value must not contain the frame terminator 0x04 (EOT); "
                        + "it would truncate the frame mid-payload.", nameof(codeValue));
                }
            }

            // 4-digit decimal length with leading zeros (per the PDF; see the empirical conclusion in the class comment)
            string lengthText = text.Length.ToString("D4");
            string payload = lengthText + text;

            return BuildOeFrame(payload);
        }

        /// <summary>Build the "query machine status" frame: 1B 31 43 3F 04 —— the heartbeat command (PDF 4.5)</summary>
        public static byte[] BuildQueryStatusFrame()
        {
            return new byte[] { 0x1B, 0x31, 0x43, 0x3F, 0x04 };
        }

        /// <summary>Build the "query printhead print count" frame: 1B 54 31 3F 04 (PDF 4.6, reserved for reconciliation)</summary>
        public static byte[] BuildQueryCountFrame()
        {
            return new byte[] { 0x1B, 0x54, 0x31, 0x3F, 0x04 };
        }

        /// <summary>Build the "clear printhead count" frame: 1B 54 31 30 04 (PDF 4.6, reserved)</summary>
        public static byte[] BuildResetCountFrame()
        {
            return new byte[] { 0x1B, 0x54, 0x31, 0x30, 0x04 };
        }

        /// <summary>
        /// Build the TCP pre-reset frame: 07 01 01 2C (4 bytes)
        /// [Origin] Field experience from Close() in the reference code —— resetting port 700 once before connecting to
        /// the data port proves more stable.
        /// </summary>
        public static byte[] BuildTcpResetFrame()
        {
            return new byte[] { 0x07, 0x01, 0x01, 0x2C };
        }

        /// <summary>
        /// Build the common skeleton of OE-type frames: 1B 4F 45 + payload ASCII + 04
        /// (shared by send-cache-data and clear-cache-queue; corresponds to the framing approach of FormatString in the
        /// reference code)
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
        // Deframing (receive direction)
        // ============================================================

        /// <summary>
        /// Try to parse the head of the buffer as a "status frame" (heartbeat reply): 1B 31 43 SSS B HHMM 04
        /// [Call convention] The first byte of the buffer is already 0x1B; when the buffer is shorter than the frame
        /// length it returns false (wait for more bytes).
        /// </summary>
        /// <param name="buffer">Receive buffer (contents are not modified; the caller decides how many bytes to consume)</param>
        /// <param name="statusCode">Output: the 3-digit status code SSS (000=ready and normal, 1xx warning, 2xx printing prohibited, 100 fault)</param>
        /// <param name="frameText">Output: a human-readable description of the frame (e.g. "1C 000 B 1432"); empty string if parsing fails</param>
        /// <returns>true = it is a complete status frame and parsing succeeded (the caller should consume STATUS_FRAME_LENGTH bytes)</returns>
        public static bool TryParseStatusFrame(List<byte> buffer, out int statusCode, out string frameText)
        {
            statusCode = -1;
            frameText = string.Empty;

            if (buffer == null || buffer.Count < STATUS_FRAME_LENGTH)
            {
                return false;
            }

            // Header signature: 1B 31 43 (ESC '1' 'C'); trailer 04
            if (buffer[0] != ESC || buffer[1] != 0x31 || buffer[2] != 0x43 || buffer[11] != EOT)
            {
                return false;
            }

            // The three SSS digits must be ASCII digits, otherwise treat it as a garbage frame
            int hundreds = buffer[3] - 0x30;
            int tens = buffer[4] - 0x30;
            int ones = buffer[5] - 0x30;

            if (hundreds < 0 || hundreds > 9 || tens < 0 || tens > 9 || ones < 0 || ones > 9)
            {
                return false;
            }

            statusCode = hundreds * 100 + tens * 10 + ones;
            frameText = "1C " + buffer[3].ToString() + buffer[4].ToString() + buffer[5].ToString()
                        + " " + ((char)buffer[6]).ToString()
                        + " " + ((char)buffer[7]).ToString() + ((char)buffer[8]).ToString()
                        + ((char)buffer[9]).ToString() + ((char)buffer[10]).ToString();

            return true;
        }

        /// <summary>
        /// Try to parse the head of the buffer as a "count frame": 1B 54 31 + 10 digits + 04
        /// [Purpose] The reply to a printhead print-count query; currently it is only logged for reconciliation and
        /// is not used for any business determination.
        /// </summary>
        /// <param name="buffer">Receive buffer</param>
        /// <param name="count">Output: the print count</param>
        /// <returns>true = it is a complete count frame and parsing succeeded (the caller should consume COUNT_FRAME_LENGTH bytes)</returns>
        public static bool TryParseCountFrame(List<byte> buffer, out long count)
        {
            count = -1;

            if (buffer == null || buffer.Count < COUNT_FRAME_LENGTH)
            {
                return false;
            }

            // Header signature: 1B 54 31 (ESC 'T' '1'); trailer 04
            if (buffer[0] != ESC || buffer[1] != 0x54 || buffer[2] != 0x31 || buffer[13] != EOT)
            {
                return false;
            }

            // Accumulate the 10 digits one by one (avoid converting the whole run with int.Parse, which would throw
            // if an individual digit is not a digit)
            long value = 0;
            for (int i = 3; i <= 12; i++)
            {
                int digit = buffer[i] - 0x30;
                if (digit < 0 || digit > 9)
                {
                    return false;
                }
                value = value * 10 + digit;
            }

            count = value;
            return true;
        }

        // ============================================================
        // Logging helpers
        // ============================================================

        /// <summary>
        /// Convert a byte array to hexadecimal text (used to log unknown bytes as-is)
        /// [Example] {0x1B, 0x0D} → "1B 0D"
        /// </summary>
        public static string ToHexString(byte[] data, int length)
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

        /// <summary>Convert a single byte to hexadecimal text (used to log byte-by-byte in packet-sticking scenarios)</summary>
        public static string ToHexByte(byte value)
        {
            return value.ToString("X2");
        }
    }
}

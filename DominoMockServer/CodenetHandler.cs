namespace DominoMockServer;

/// <summary>
/// Command-level facade over the mock printer's protocol handling.
///
/// <para>
/// <b>Role.</b> <see cref="MockPrinter"/> owns the socket and the session lifecycle;
/// this type owns the <i>protocol semantics</i> - which command a frame represents and
/// what answer it deserves. Keeping the two apart means the emulated rules can be
/// unit-tested in isolation, without opening a socket.
/// </para>
///
/// <para>
/// The protocol shape emulated here is the one observed on the wire:
/// a command frame is <c>1B &lt;cmd&gt; ... 04</c>, the answer is a bare
/// <c>0x06</c> for acceptance or <c>0x15</c> for refusal, and a finished job
/// produces an unsolicited <c>0x32</c>.
/// </para>
/// </summary>
public sealed class CodenetHandler
{
    private const byte ESC = 0x1B;

    /// <summary>Optional sink for malformed-frame diagnostics. Set to null to disable.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The kind of command a frame encodes.</summary>
    public enum CommandKind
    {
        /// <summary>Not a recognisable Codenet command frame.</summary>
        Unknown = 0,

        /// <summary>Print-signal setup (<c>1B 49 31 32 04</c>).</summary>
        SignalSetup = 1,

        /// <summary>FIFO depth query.</summary>
        FifoQuery = 2,

        /// <summary>Cache queue clear.</summary>
        ClearQueue = 3,

        /// <summary>Send cached data, i.e. submit a print job.</summary>
        PrintJob = 4
    }

    /// <summary>The outcome of interpreting a frame.</summary>
    public sealed class ParsedCommand
    {
        /// <summary>Which command the frame encodes.</summary>
        public CommandKind Kind { get; init; }

        /// <summary>For <see cref="CommandKind.PrintJob"/>, the code text to print.</summary>
        public string Payload { get; init; } = string.Empty;

        /// <summary>For <see cref="CommandKind.ClearQueue"/>, the queue index.</summary>
        public int QueueIndex { get; init; }
    }

    /// <summary>
    /// Interpret a raw frame.
    /// </summary>
    /// <param name="frame">A complete frame including header and terminator.</param>
    /// <returns>The parsed command; <see cref="CommandKind.Unknown"/> when the frame
    /// does not match any emulated command.</returns>
    public ParsedCommand Parse(byte[] frame)
    {
        if (frame == null || frame.Length < 4 || frame[0] != ESC)
        {
            return new ParsedCommand { Kind = CommandKind.Unknown };
        }

        // Print-signal setup is a fixed five-byte frame with no payload.
        if (frame.Length >= 5 && frame[1] == 0x49 && frame[2] == 0x31 && frame[3] == 0x32)
        {
            return new ParsedCommand { Kind = CommandKind.SignalSetup };
        }

        // Everything else in this demo is an OE-family frame.
        if (frame[1] != 0x4F || frame[2] != 0x45)
        {
            return new ParsedCommand { Kind = CommandKind.Unknown };
        }

        // Take the ASCII payload between the 3-byte header and the terminator.
        int payloadLength = frame.Length - 4;
        if (payloadLength <= 0)
        {
            return new ParsedCommand { Kind = CommandKind.Unknown };
        }

        string payload = System.Text.Encoding.ASCII.GetString(frame, 3, payloadLength);

        if (payload == "00017")
        {
            return new ParsedCommand { Kind = CommandKind.FifoQuery };
        }

        if (payload.Length >= 5 && payload.StartsWith("0000", StringComparison.Ordinal))
        {
            int queueIndex = payload[4] - '0';

            if (queueIndex >= 0 && queueIndex <= 2)
            {
                return new ParsedCommand { Kind = CommandKind.ClearQueue, QueueIndex = queueIndex };
            }
        }

        // Send-cached-data: four decimal length digits followed by that many
        // characters of code text.
        // [2026-09-17] The four leading digits are only a length field once they are
        // all decimal digits (F11). "00017" (FIFO query) and "0000X" (clear queue) are
        // fixed literals that were handled above; anything else that is not four digits
        // is rejected outright instead of being coerced through int.TryParse.
        if (payload.Length >= 4 && AreAllDecimalDigits(payload, 4)
            && int.TryParse(payload.Substring(0, 4), out int declaredLength))
        {
            // [2026-09-16] The declared length must match the bytes actually present
            // (F5). A mismatch (truncated frame, corrupt length field) is rejected
            // gracefully instead of letting Substring throw an out-of-range exception.
            // [2026-09-17] The comparison is now exact rather than "at least": trailing
            // bytes after the declared payload mean the length field is not trustworthy,
            // which is precisely the condition that made the old parser fragile (F11).
            if (payload.Length != 4 + declaredLength)
            {
                Log?.Invoke("Malformed OE frame: declared length " + declaredLength.ToString()
                    + " but " + (payload.Length - 4).ToString()
                    + " code bytes present; rejecting.");
                return new ParsedCommand { Kind = CommandKind.Unknown };
            }

            return new ParsedCommand
            {
                Kind = CommandKind.PrintJob,
                Payload = payload.Substring(4, declaredLength)
            };
        }

        return new ParsedCommand { Kind = CommandKind.Unknown };
    }

    /// <summary>
    /// Whether the first <paramref name="count"/> characters of <paramref name="text"/>
    /// are all ASCII decimal digits.
    /// </summary>
    private static bool AreAllDecimalDigits(string text, int count)
    {
        if (text.Length < count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (text[i] < '0' || text[i] > '9')
            {
                return false;
            }
        }

        return true;
    }
}

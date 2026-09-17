using DominoA200Sdk.Core;
using Xunit;

namespace DominoSdk.Harness.TestCases;

/// <summary>
/// Verifies the Codenet framing rules in isolation, with no socket involved.
///
/// <para>
/// <b>Why these tests exist.</b> Two framing defects were found during review and are
/// covered here so they cannot come back:
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Terminator inside the payload.</b> The receiver locates the end of a frame by
///   scanning for the first <c>0x04</c>. A payload containing <c>0x04</c> therefore used
///   to be truncated mid-frame and the tail silently discarded. The builder now rejects
///   such a payload outright.
///   </description></item>
///   <item><description>
///   <b>Blindly trusted length field.</b> The four digits after the <c>OE</c> header are
///   the length of the <i>code text only</i>. Frames that do not line up with the
///   terminator are now treated as incomplete instead of being consumed on a guess.
///   </description></item>
/// </list>
/// </summary>
public sealed class FramingTests
{
    private const byte ESC = 0x1B;
    private const byte EOT = 0x04;

    // ============================================================
    // Payload validation
    // ============================================================

    /// <summary>
    /// A payload containing <c>0x04</c> must be rejected: it would truncate the frame.
    /// </summary>
    [Theory]
    [InlineData("AB\u0004CDEF")]
    [InlineData("\u0004")]
    [InlineData("ABC\u0004")]
    public void BuildPrintJobFrame_RejectsTerminatorInsidePayload(string code)
    {
        Assert.Throws<ArgumentException>(() => CodenetFrame.BuildPrintJobFrame(code));
    }

    /// <summary>
    /// <c>0x1B</c> (ESC) inside the payload is harmless and must still be accepted; only
    /// the terminator is ambiguous.
    /// </summary>
    [Fact]
    public void BuildPrintJobFrame_AllowsEscInsidePayload()
    {
        byte[] frame = CodenetFrame.BuildPrintJobFrame("AB\u001bCDEF");

        Assert.Equal(ESC, frame[0]);
        Assert.Equal(EOT, frame[frame.Length - 1]);
        Assert.Equal(15, frame.Length);
    }

    /// <summary>A null, empty or non-ASCII payload must be rejected.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABC\u4e2d")]
    public void BuildPrintJobFrame_RejectsInvalidPayload(string? code)
    {
        Assert.Throws<ArgumentException>(() => CodenetFrame.BuildPrintJobFrame(code!));
    }

    /// <summary>
    /// The 4-digit length field caps the payload at 9999 characters; one more must fail.
    /// </summary>
    [Fact]
    public void BuildPrintJobFrame_EnforcesMaximumLength()
    {
        byte[] max = CodenetFrame.BuildPrintJobFrame(new string('A', 9999));

        // 3 header + 4 length digits + 9999 code bytes + 1 terminator.
        Assert.Equal(3 + 4 + 9999 + 1, max.Length);
        Assert.Throws<ArgumentException>(() => CodenetFrame.BuildPrintJobFrame(new string('A', 10000)));
    }

    /// <summary>
    /// The length field must describe the code text only, not the four length digits.
    /// This is the exact assumption the parser relies on.
    /// </summary>
    [Fact]
    public void BuildPrintJobFrame_LengthFieldCountsCodeTextOnly()
    {
        byte[] frame = CodenetFrame.BuildPrintJobFrame("2026-09-15 ABC000001");

        Assert.Equal("0020", System.Text.Encoding.ASCII.GetString(frame, 3, 4));
        Assert.Equal(3 + 4 + 20 + 1, frame.Length);
    }

    // ============================================================
    // Frame parsing
    // ============================================================

    /// <summary>Every real frame type must round-trip byte-for-byte.</summary>
    [Fact]
    public void TryParseFrame_RoundTripsEveryFrameType()
    {
        byte[][] frames =
        {
            CodenetFrame.BuildSignalSetupFrame(),
            CodenetFrame.BuildFifoQueryFrame(),
            CodenetFrame.BuildClearQueueFrame(0),
            CodenetFrame.BuildClearQueueFrame(2),
            CodenetFrame.BuildPrintJobFrame("2026-09-15 ABC000001"),
            CodenetFrame.BuildPrintJobFrame("X"),
            CodenetFrame.BuildPrintJobFrame(new string('A', 9999))
        };

        foreach (byte[] frame in frames)
        {
            bool parsed = CodenetFrame.TryParseFrame(new List<byte>(frame), out byte[] actual);

            Assert.True(parsed, "A complete frame must parse.");
            Assert.Equal(frame, actual);
        }
    }

    /// <summary>
    /// A frame split across reads must report "incomplete" at every cut point and
    /// reassemble exactly once the remainder arrives.
    /// </summary>
    [Fact]
    public void TryParseFrame_HandlesEverySplitPoint()
    {
        byte[] frame = CodenetFrame.BuildPrintJobFrame("2026-09-15 ABC000001");

        for (int cut = 1; cut < frame.Length; cut++)
        {
            List<byte> partial = new List<byte>(frame[..cut]);

            Assert.False(CodenetFrame.TryParseFrame(partial, out _));

            List<byte> reassembled = new List<byte>(frame[..cut]);
            reassembled.AddRange(frame[cut..]);

            Assert.True(CodenetFrame.TryParseFrame(reassembled, out byte[] actual));
            Assert.Equal(frame, actual);
        }
    }

    /// <summary>
    /// A lying length field must not be consumed. The declared length does not line up
    /// with a terminator, so the parser reports "wait for more bytes" rather than
    /// returning a frame whose payload cannot be trusted.
    /// </summary>
    [Fact]
    public void TryParseFrame_RejectsLyingLengthField()
    {
        // Declares 9 code bytes but only 4 are present before the terminator.
        List<byte> buffer = new List<byte> { ESC, 0x4F, 0x45 };
        buffer.AddRange(System.Text.Encoding.ASCII.GetBytes("0009ABCD"));
        buffer.Add(EOT);

        Assert.False(CodenetFrame.TryParseFrame(buffer, out _));
    }

    /// <summary>
    /// Several frames arriving in one coalesced read must be split on their declared
    /// boundaries, in order.
    /// </summary>
    [Fact]
    public void TryParseFrame_SplitsCoalescedFramesInOrder()
    {
        byte[] signal = CodenetFrame.BuildSignalSetupFrame();
        byte[] job = CodenetFrame.BuildPrintJobFrame("2026-09-15 ABC000001");
        byte[] query = CodenetFrame.BuildFifoQueryFrame();

        List<byte> buffer = new List<byte>();
        buffer.AddRange(signal);
        buffer.AddRange(job);
        buffer.AddRange(query);

        List<byte[]> parsed = new List<byte[]>();

        while (CodenetFrame.TryParseFrame(buffer, out byte[] frame))
        {
            parsed.Add(frame);
            buffer.RemoveRange(0, frame.Length);
        }

        Assert.Equal(3, parsed.Count);
        Assert.Equal(signal, parsed[0]);
        Assert.Equal(job, parsed[1]);
        Assert.Equal(query, parsed[2]);
        Assert.Empty(buffer);
    }

    /// <summary>
    /// Stray bytes before a valid frame must be skipped without corrupting the frame.
    /// </summary>
    [Fact]
    public void TryParseFrame_ResynchronisesAfterGarbage()
    {
        byte[] job = CodenetFrame.BuildPrintJobFrame("2026-09-15 ABC000001");

        List<byte> buffer = new List<byte> { 0x00, 0xFF, 0x41 };
        buffer.AddRange(job);

        // Drop the leading junk exactly as the receive loops do.
        while (buffer.Count > 0 && buffer[0] != ESC)
        {
            buffer.RemoveAt(0);
        }

        Assert.True(CodenetFrame.TryParseFrame(buffer, out byte[] actual));
        Assert.Equal(job, actual);
    }
}

namespace DominoA200Sdk.Models;

/// <summary>
/// An immutable print job submitted to the printer.
///
/// <para>
/// The A200+ prints a code value that the on-board template expands into the final
/// marking. The SDK therefore only carries the text: layout, font and barcode
/// encapsulation all belong to the printer-side template.
/// </para>
/// </summary>
public sealed class PrintJob
{
    /// <summary>
    /// Create a print job.
    /// </summary>
    /// <param name="dateText">The date field value, typically <c>yyyy-MM-dd</c>.</param>
    /// <param name="codeValue">The code text to print. Must not be null or empty.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="codeValue"/> is null or whitespace.</exception>
    public PrintJob(string dateText, string codeValue)
    {
        if (string.IsNullOrWhiteSpace(codeValue))
        {
            throw new ArgumentException("Code value must not be null or empty.", nameof(codeValue));
        }

        DateText = dateText ?? string.Empty;
        CodeValue = codeValue;
    }

    /// <summary>The date field value shown alongside the code.</summary>
    public string DateText { get; }

    /// <summary>The code text the printer will mark.</summary>
    public string CodeValue { get; }

    /// <summary>
    /// Render this job as the payload the A200+ expects for a "send cached data"
    /// command: the date and the code joined by a single space.
    /// </summary>
    public string ToPayload()
    {
        if (string.IsNullOrEmpty(DateText))
        {
            return CodeValue;
        }

        return DateText + " " + CodeValue;
    }

    /// <summary>Short diagnostic description.</summary>
    public override string ToString()
    {
        return "PrintJob[" + ToPayload() + "]";
    }
}

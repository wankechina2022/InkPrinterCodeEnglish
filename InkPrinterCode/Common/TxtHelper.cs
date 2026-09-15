using System.Text;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Text file reading helper -- dedicated to importing txt code data
    ///
    /// [File format] One code per line, separated by CRLF, no header, no other fields.
    ///
    /// [Mechanism]
    ///   - Lines are read one at a time with StreamReader rather than File.ReadAllLines, because the latter keeps
    ///     both the "whole file string" and the "line array" in memory, doubling peak memory for a 100k-line file for no reason.
    ///   - Opening with FileShare.ReadWrite lets other programs hold the file at the same time (for example a user viewing it in Notepad),
    ///     so reading does not simply fail because the file is in use.
    ///   - The cancel token is checked every 2000 lines so that clicking "Cancel" stops the work fairly quickly, while still
    ///     avoiding the slowdown of checking the token on every single line.
    ///
    /// [Encoding] Files are read as UTF-8 with BOM auto-detection enabled (detectEncodingFromByteOrderMarks=true):
    ///   UTF-8/UTF-16 files with a BOM are read using their actual encoding; files without a BOM are read as UTF-8.
    ///   Code values are confirmed to contain no Chinese (digits and letters only), so GBK support is unnecessary and the CodePages package does not have to be pulled in.
    ///
    /// [Boundaries]
    ///   - Empty path / missing file -> throw, so the BLL can convert it into a friendly message (this is a caller argument error and must not be silent);
    ///   - Blank lines still count towards the total and are added to the result set (preserving line-number correspondence); ValidateHelper decides whether they are invalid;
    ///   - User cancellation -> throw OperationCanceledException, which the BLL catches, rolls back and marks as Canceled.
    /// </summary>
    public static class TxtHelper
    {
        /// <summary>Line interval for cancel checks</summary>
        private const int CANCEL_CHECK_STEP = 2000;

        /// <summary>
        /// Read all lines of a txt file
        /// </summary>
        /// <param name="filePath">Full path of the file</param>
        /// <param name="lines">Collection that receives the result (created by the caller; this method only adds to it)</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Total number of lines read (equal to the number of items added to lines)</returns>
        public static int ReadAllLines(string filePath, List<string> lines, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("The txt file path cannot be empty", nameof(filePath));
            }
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines), "The result collection cannot be null");
            }
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("The txt file to import does not exist: " + filePath, filePath);
            }

            int totalRows = 0;
            FileStream? stream = null;
            StreamReader? reader = null;

            try
            {
                stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                reader = new StreamReader(stream, new UTF8Encoding(false), true);

                while (true)
                {
                    string? line = reader.ReadLine();
                    if (line == null)
                    {
                        break;
                    }

                    totalRows++;
                    lines.Add(line);

                    if (totalRows % CANCEL_CHECK_STEP == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            finally
            {
                // Each releases its own resource: StreamReader closes the underlying stream along with it, so closing it
                // again explicitly here is also idempotent, and this guarantees that stream is still released if reader
                // creation failed.
                if (reader != null)
                {
                    reader.Dispose();
                }
                if (stream != null)
                {
                    stream.Dispose();
                }
            }

            return totalRows;
        }
    }
}

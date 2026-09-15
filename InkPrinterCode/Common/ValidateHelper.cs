namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Validation helper class —— centralizes the code-value cleaning and validity checks used in the import flow
    ///
    /// [Code-value rules (confirmed by Mr. Wan on 2026-09-10)]
    ///   1. Cleaning only does a Trim (removing leading and trailing whitespace, including leftover spaces, tabs and carriage returns);
    ///   2. Empty after Trim → a blank line; counted as invalid and skipped;
    ///   3. Contains Chinese characters → ignored outright, counted as invalid and skipped (on-site code values never
    ///      contain Chinese, so any occurrence is treated as dirty data);
    ///   4. No length limit and no character-set restriction —— anything other than the two cases above is allowed through.
    ///
    /// [Chinese detection ranges]
    ///   \u4E00-\u9FFF  CJK Unified Ideographs (covers the common Chinese characters)
    ///   \u3400-\u4DBF  CJK Extension A (rare characters)
    ///   \u3000-\u303F  CJK punctuation (including the ideographic space, book-title marks, enumeration commas, etc.)
    ///   \uFF00-\uFFEF  Full-width characters (full-width parentheses, full-width digits and letters, etc.)
    ///   This blocks Chinese characters as well as dirty data that "looks half-width but is actually full-width".
    ///
    /// [Boundaries] Every method accepts null input and handles it, and never throws a null reference exception.
    /// </summary>
    public static class ValidateHelper
    {
        /// <summary>
        /// Clean a raw line —— Trim only
        /// </summary>
        /// <param name="rawLine">The raw content read from the source file; may be null</param>
        /// <returns>The trimmed string; a null input returns an empty string</returns>
        public static string NormalizeCode(string? rawLine)
        {
            if (rawLine == null)
            {
                return string.Empty;
            }
            return rawLine.Trim();
        }

        /// <summary>
        /// Determine whether a string contains Chinese / full-width characters
        /// [Implementation] A plain for loop compares each character against the Unicode ranges; even 100k lines is a single linear scan with negligible cost.
        /// </summary>
        /// <param name="value">String to test; may be null</param>
        /// <returns>true if it contains one; false for null or an empty string</returns>
        public static bool ContainsChinese(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                if (c >= '\u4E00' && c <= '\u9FFF')
                {
                    return true;
                }
                if (c >= '\u3400' && c <= '\u4DBF')
                {
                    return true;
                }
                if (c >= '\u3000' && c <= '\u303F')
                {
                    return true;
                }
                if (c >= '\uFF00' && c <= '\uFFEF')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determine whether a cleaned code value is valid
        /// </summary>
        /// <param name="trimmedValue">Code value already processed by NormalizeCode</param>
        /// <param name="invalidReason">Reason for invalidity: "Empty row" or "Contains Chinese characters"; empty string when valid</param>
        /// <returns>true if valid</returns>
        public static bool IsValidCode(string? trimmedValue, out string invalidReason)
        {
            invalidReason = string.Empty;

            if (string.IsNullOrEmpty(trimmedValue))
            {
                invalidReason = "Empty row";
                return false;
            }

            if (ContainsChinese(trimmedValue))
            {
                invalidReason = "contains Chinese characters";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determine whether a file exists and is accessible
        /// [Purpose] Existence check before importing (development rule: always check that the target exists before copying, deleting, importing or exporting a file).
        /// </summary>
        /// <param name="filePath">Full path of the file</param>
        /// <returns>true when the path is not empty and the file really exists</returns>
        public static bool IsFileExists(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            try
            {
                return File.Exists(filePath);
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("Error while checking whether the file exists: " + filePath + ", " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Ensure a directory exists, creating it when it does not
        /// [Purpose] Called uniformly before writing a database file, exporting Excel, or writing logs (development
        /// standard: create the directory automatically before writing a file if it does not exist).
        /// </summary>
        /// <param name="folderPath">Full directory path</param>
        /// <returns>Returns true if the directory already existed or was created successfully; returns false if the path is empty or creation failed</returns>
        public static bool EnsureFolderExists(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            try
            {
                if (Directory.Exists(folderPath))
                {
                    return true;
                }
                Directory.CreateDirectory(folderPath);
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to create directory: " + folderPath, ex);
                return false;
            }
        }
    }
}

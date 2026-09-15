using InkPrinterCode.Model;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Enum conversion helper class —— all conversions between enums and UI text, database int values,
    /// and file extensions are centralized here
    ///
    /// [Why centralized] The development standard requires "use enums wherever possible, and write conversion classes
    ///   as public methods". If the status display names were scattered as switch statements in each form, changing
    ///   the wording would mean hunting through the whole project; centralized in one place, one change takes effect
    ///   globally.
    ///
    /// [Style note] Traditional switch statements with an explicit default branch are used uniformly, not switch
    ///   expressions, so that line-by-line debugging is easy. The default branch does not throw but instead returns
    ///   the text "Unknown(value)" —— should an unexpected numeric value ever appear in the database, the UI can still
    ///   display it (as "Unknown(9)") rather than one dirty record preventing the entire list from rendering.
    /// </summary>
    public static class EnumHelper
    {
        /// <summary>Convention: the value of the "All" option in a dropdown</summary>
        public const int ALL_OPTION_VALUE = -1;

        // ============================================================
        // Print status
        // ============================================================

        /// <summary>
        /// Get the display text for a print status
        /// </summary>
        public static string GetPrintStatusText(PrintStatus status)
        {
            switch (status)
            {
                case PrintStatus.NotPrinted:
                    return "Not Printed";
                case PrintStatus.Printed:
                    return "Printed";
                case PrintStatus.Failed:
                    return "Failed";
                case PrintStatus.Voided:
                    return "Voided";
                default:
                    return "Unknown(" + ((int)status).ToString() + ")";
            }
        }

        /// <summary>
        /// Get the display text for a print status (int overload)
        /// [Purpose] DataGridView binds the int value read from the database; use this overload to convert it to text
        /// directly, saving an external cast.
        /// </summary>
        public static string GetPrintStatusText(int statusValue)
        {
            return GetPrintStatusText(ParsePrintStatus(statusValue));
        }

        /// <summary>
        /// Convert an int value from the database into the enum
        /// [Boundaries] It does not throw on an undefined numeric value; it casts and returns it as-is (which
        ///   GetPrintStatusText then displays as "Unknown(value)"), letting the UI expose the dirty data instead of
        ///   letting the program crash.
        /// </summary>
        public static PrintStatus ParsePrintStatus(int statusValue)
        {
            switch (statusValue)
            {
                case 0:
                    return PrintStatus.NotPrinted;
                case 1:
                    return PrintStatus.Printed;
                case 2:
                    return PrintStatus.Failed;
                case 3:
                    return PrintStatus.Voided;
                default:
                    return (PrintStatus)statusValue;
            }
        }

        /// <summary>
        /// Build the data source for a print-status dropdown
        /// </summary>
        /// <param name="includeAllOption">Whether to prepend an "All" option (value -1)</param>
        public static List<ComboItem> GetPrintStatusItems(bool includeAllOption)
        {
            List<ComboItem> items = new List<ComboItem>();

            if (includeAllOption)
            {
                items.Add(new ComboItem(ALL_OPTION_VALUE, "All"));
            }

            items.Add(new ComboItem((int)PrintStatus.NotPrinted, GetPrintStatusText(PrintStatus.NotPrinted)));
            items.Add(new ComboItem((int)PrintStatus.Printed, GetPrintStatusText(PrintStatus.Printed)));
            items.Add(new ComboItem((int)PrintStatus.Failed, GetPrintStatusText(PrintStatus.Failed)));
            items.Add(new ComboItem((int)PrintStatus.Voided, GetPrintStatusText(PrintStatus.Voided)));

            return items;
        }

        // ============================================================
        // Import source type
        // ============================================================

        /// <summary>
        /// Get the display text for an import source type
        /// </summary>
        public static string GetSourceTypeText(ImportSourceType sourceType)
        {
            switch (sourceType)
            {
                case ImportSourceType.Txt:
                    return "Text file";
                case ImportSourceType.Excel:
                    return "Excel file";
                default:
                    return "Unknown(" + ((int)sourceType).ToString() + ")";
            }
        }

        /// <summary>
        /// Determine the import source type from the file extension
        ///
        /// [Dual-path note]
        ///   Supported extensions: .txt → Txt; .xls / .xlsx → Excel, with supported returning true;
        ///   Other extensions (including no extension and an empty path): supported returns false, in which case the
        ///   return value is meaningless and the caller must reject the import outright —— "guessing" the format and
        ///   forcing a read is not allowed, to avoid reading a binary file as text and importing screens full of
        ///   garbage into the database.
        /// </summary>
        /// <param name="filePath">File path</param>
        /// <param name="supported">Whether this is a supported format</param>
        /// <returns>Returns the corresponding source type when supported; returns Txt when unsupported (meaningless; the caller must check supported first)</returns>
        public static ImportSourceType GetSourceTypeByExtension(string? filePath, out bool supported)
        {
            supported = false;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return ImportSourceType.Txt;
            }

            string extension = string.Empty;
            try
            {
                extension = Path.GetExtension(filePath).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("Failed to parse the file extension: " + filePath + ", " + ex.Message);
                return ImportSourceType.Txt;
            }

            if (extension == ".txt")
            {
                supported = true;
                return ImportSourceType.Txt;
            }

            if (extension == ".xls" || extension == ".xlsx")
            {
                supported = true;
                return ImportSourceType.Excel;
            }

            return ImportSourceType.Txt;
        }
    }
}

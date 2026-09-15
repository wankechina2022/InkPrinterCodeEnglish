using System.Data;
using System.Globalization;
using NPOI.SS.UserModel;
using NPOI.XSSF.Streaming;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Excel read/write helper class (based on NPOI) —— reads the first column for import, exports query results
    ///
    /// [Import format (confirmed by Mr. Wan)] Only the first column of the first Sheet is taken, with no header row;
    /// all other columns are ignored.
    ///
    /// [Reading mechanism]
    ///   - WorkbookFactory.Create automatically recognizes .xls (HSSF) and .xlsx (XSSF), so the caller needs no branching.
    ///   - The first column is taken row by row, with blank rows occupying an empty string, guaranteeing that
    ///     "result index + 1 = Excel row number" so that troubleshooting lines up with the original position.
    ///   - The cancellation signal is checked every 2000 rows.
    ///
    /// [Key pitfall: scientific notation for numeric cells]
    ///   An Excel numeric cell is a double underneath. If cell.ToString() is called directly, or the double is
    ///   ToString()'d directly, an 18-digit code becomes "1.23457E+17" in the database and the data is ruined.
    ///   This class's FormatNumericCell always outputs integers using long, avoiding scientific notation.
    ///   ⚠ But this only saves "formatting problems", not "precision problems": a double can only represent 15~16
    ///   digit integers exactly, so if a code exceeds 15 digits and is stored in Excel in the "Number" format, Excel
    ///   itself has already discarded the trailing digits. Hence the on-site convention: the code-value column in
    ///   Excel must be set to the "Text" format (CellType.String) to guarantee long codes are lossless.
    ///
    /// [Export mechanism] SXSSFWorkbook is used for streaming writes (only a 1000-row sliding window is kept in
    ///   memory), so exporting 100,000 rows will not blow up memory; every cell is written as a string, so that the
    ///   exported codes are not recognized as numbers in Excel and turned into scientific notation again.
    ///
    /// [Resource release] NPOI's workbook and file streams are all unmanaged-related resources and are explicitly
    ///   released with try/finally; SXSSFWorkbook must also be Disposed to clear the temporary files it produced,
    ///   otherwise the temp directory keeps growing.
    /// </summary>
    public static class ExcelHelper
    {
        /// <summary>Row interval for the cancellation check</summary>
        private const int CANCEL_CHECK_STEP = 2000;

        // Note: SXSSFWorkbook uses the parameterless constructor —— NPOI's default in-memory sliding window (100 rows);
        // rows beyond the window automatically spill to a temporary file, so exporting 100,000 rows keeps memory steady.
        // A custom window size is deliberately not passed here, to avoid depending on a specific overload signature and
        // thus reduce the compile risk when upgrading versions.

        // ============================================================
        // Reading (for import)
        // ============================================================

        /// <summary>
        /// Read the first column of the first Sheet of an Excel file
        /// </summary>
        /// <param name="filePath">Full path of the Excel file (.xls / .xlsx)</param>
        /// <param name="values">The collection receiving the results (created by the caller; the method only Adds to it)</param>
        /// <param name="token">Cancellation signal</param>
        /// <returns>Total number of rows read (equal to the number of items added to values)</returns>
        public static int ReadFirstColumn(string filePath, List<string> values, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("The Excel file path cannot be empty", nameof(filePath));
            }
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values), "The collection receiving the results cannot be null");
            }
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("The Excel file to import does not exist: " + filePath, filePath);
            }

            int totalRows = 0;
            FileStream? stream = null;
            IWorkbook? workbook = null;

            try
            {
                stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                workbook = WorkbookFactory.Create(stream);

                if (workbook.NumberOfSheets <= 0)
                {
                    LogHelper.Instance.Warn("The Excel file contains no worksheet: " + filePath);
                    return 0;
                }

                ISheet sheet = workbook.GetSheetAt(0);
                if (sheet == null)
                {
                    LogHelper.Instance.Warn("The first Excel worksheet read back empty: " + filePath);
                    return 0;
                }

                // LastRowNum is the zero-based index of the last row, so the total row count = LastRowNum + 1
                int lastRowIndex = sheet.LastRowNum;

                for (int rowIndex = 0; rowIndex <= lastRowIndex; rowIndex++)
                {
                    totalRows++;

                    IRow row = sheet.GetRow(rowIndex);
                    if (row == null)
                    {
                        // The whole row is empty: occupy the slot with an empty string to preserve the row-number correspondence
                        values.Add(string.Empty);
                    }
                    else
                    {
                        ICell cell = row.GetCell(0);
                        values.Add(GetCellText(cell));
                    }

                    if (totalRows % CANCEL_CHECK_STEP == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            finally
            {
                if (workbook != null)
                {
                    try
                    {
                        workbook.Close();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("Failed to close the Excel workbook: " + ex.Message);
                    }

                    IDisposable? disposableWorkbook = workbook as IDisposable;
                    if (disposableWorkbook != null)
                    {
                        try
                        {
                            disposableWorkbook.Dispose();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("Failed to dispose the Excel workbook: " + ex.Message);
                        }
                    }
                }

                if (stream != null)
                {
                    stream.Dispose();
                }
            }

            return totalRows;
        }

        // ============================================================
        // Export (used by the data view page)
        // ============================================================

        /// <summary>
        /// Export a DataTable to an xlsx file
        ///
        /// [Boundaries]
        ///   - The target directory is created automatically when it does not exist (required by the development standard);
        ///   - When table is null or has no columns, an exception is thrown outright, to avoid generating an empty shell
        ///     file that misleads the user;
        ///   - An existing target file is overwritten; whether to overwrite is for the caller (SaveFileDialog) to
        ///     confirm with the user.
        /// </summary>
        /// <param name="table">The data to export; the column names are the headers</param>
        /// <param name="filePath">Save path (.xlsx)</param>
        /// <param name="sheetName">Worksheet name; uses Sheet1 when empty</param>
        public static void ExportDataTable(DataTable table, string filePath, string sheetName)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table), "The data to export cannot be null");
            }
            if (table.Columns.Count <= 0)
            {
                throw new ArgumentException("The data to export has no columns at all", nameof(table));
            }
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("The export path cannot be empty", nameof(filePath));
            }

            string? folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                if (!ValidateHelper.EnsureFolderExists(folder))
                {
                    throw new IOException("The export directory is unavailable: " + folder);
                }
            }

            string finalSheetName = "Sheet1";
            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                finalSheetName = sheetName;
            }

            SXSSFWorkbook? workbook = null;
            FileStream? stream = null;

            try
            {
                workbook = new SXSSFWorkbook();
                ISheet sheet = workbook.CreateSheet(finalSheetName);

                // Header row
                IRow headerRow = sheet.CreateRow(0);
                for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                {
                    ICell headerCell = headerRow.CreateCell(columnIndex);
                    headerCell.SetCellValue(table.Columns[columnIndex].ColumnName);
                }

                // Data rows: everything is written as a string, preventing long codes from being recognized as numbers
                // by Excel and displayed in scientific notation
                for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    IRow dataRow = sheet.CreateRow(rowIndex + 1);
                    for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                    {
                        ICell dataCell = dataRow.CreateCell(columnIndex);
                        dataCell.SetCellValue(table.Rows[rowIndex][columnIndex].ToSafeString());
                    }
                }

                stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                // The second parameter leaveOpen=false: let NPOI finish up after writing, and the file stream is then
                // explicitly released once more in this method's finally (idempotent)
                workbook.Write(stream, false);
            }
            finally
            {
                if (workbook != null)
                {
                    try
                    {
                        workbook.Close();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("Failed to close the export workbook: " + ex.Message);
                    }

                    // SXSSFWorkbook writes rows beyond the in-memory window to a temporary file, which only Dispose can clean up
                    IDisposable? disposableWorkbook = workbook as IDisposable;
                    if (disposableWorkbook != null)
                    {
                        try
                        {
                            disposableWorkbook.Dispose();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("Failed to clean up the export workbook's temporary files: " + ex.Message);
                        }
                    }
                }

                if (stream != null)
                {
                    stream.Dispose();
                }
            }
        }

        // ============================================================
        // Private: cell value retrieval
        // ============================================================

        /// <summary>
        /// Get the cell text
        /// [Dual path] An ordinary cell takes its value according to its CellType; a formula cell takes the "cached
        ///   result" rather than recalculating —— recalculating formulas across 100,000 rows is extremely slow, and the
        ///   import scenario only cares about the currently displayed value.
        /// </summary>
        private static string GetCellText(ICell? cell)
        {
            if (cell == null)
            {
                return string.Empty;
            }

            switch (cell.CellType)
            {
                case CellType.String:
                    if (cell.StringCellValue == null)
                    {
                        return string.Empty;
                    }
                    return cell.StringCellValue;

                case CellType.Numeric:
                    return FormatNumericCell(cell);

                case CellType.Boolean:
                    if (cell.BooleanCellValue)
                    {
                        return "TRUE";
                    }
                    return "FALSE";

                case CellType.Formula:
                    return GetFormulaCellText(cell);

                case CellType.Blank:
                    return string.Empty;

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Convert a numeric cell to text —— avoiding scientific notation
        /// [Mechanism] An integer (zero fractional part) is output with long; a value that genuinely has a fractional
        /// part is output with at most 10 decimal places, without trailing zeros.
        /// [Boundaries] NaN / infinity returns an empty string (the layer above treats such values as a blank row and skips it).
        /// </summary>
        private static string FormatNumericCell(ICell cell)
        {
            double numericValue = 0d;
            try
            {
                numericValue = cell.NumericCellValue;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to read the numeric cell: " + ex.Message);
                return string.Empty;
            }

            if (double.IsNaN(numericValue) || double.IsInfinity(numericValue))
            {
                return string.Empty;
            }

            bool isInteger = Math.Abs(numericValue - Math.Floor(numericValue)) < 0.0000001;
            bool inLongRange = Math.Abs(numericValue) < 9.0E18;

            if (isInteger && inLongRange)
            {
                long longValue = (long)numericValue;
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            return numericValue.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A formula cell takes the cached result
        /// [Boundaries] Any exception during value retrieval returns an empty string; a single formula cell must not
        /// abort the whole import.
        /// </summary>
        private static string GetFormulaCellText(ICell cell)
        {
            try
            {
                switch (cell.CachedFormulaResultType)
                {
                    case CellType.String:
                        if (cell.StringCellValue == null)
                        {
                            return string.Empty;
                        }
                        return cell.StringCellValue;

                    case CellType.Numeric:
                        return FormatNumericCell(cell);

                    case CellType.Boolean:
                        if (cell.BooleanCellValue)
                        {
                            return "TRUE";
                        }
                        return "FALSE";

                    default:
                        return string.Empty;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to read the formula cell: " + ex.Message);
                return string.Empty;
            }
        }
    }
}

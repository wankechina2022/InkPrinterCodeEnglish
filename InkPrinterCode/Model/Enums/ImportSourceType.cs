namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] Data import source type enumeration
    ///
    /// [Value convention] The numbers map one-to-one to the ImportBatch.SourceType column in the database:
    ///   0 = Txt   : plain text file, one code per line, separated by CRLF
    ///   1 = Excel : Excel file (.xls / .xlsx); only the first column of the first sheet is read, with no header row
    ///
    /// [Boundary] Unsupported extensions do not fall into this enumeration; EnumHelper.GetSourceTypeByExtension
    ///          returns supported=false and the caller must reject the import outright rather than guess the format.
    /// </summary>
    public enum ImportSourceType
    {
        /// <summary>Plain text file (0) -- one code per line</summary>
        Txt = 0,

        /// <summary>Excel file (1) -- first column of the first sheet, no header row</summary>
        Excel = 1
    }
}

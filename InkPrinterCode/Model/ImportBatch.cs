namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] Import batch ledger entity -- maps to the SQLite table ImportBatch
    ///
    /// [Purpose] Purely for background traceability: it records the file source and four counters for each import,
    ///   so when something goes wrong it is possible to find out "which file this batch of codes came from, when it
    ///   was imported, by whom, and how many rows were valid". The data view page does not provide batch filtering,
    ///   and the batch concept is not exposed on the UI.
    ///
    /// [Write timing] One row is inserted when the import starts (to obtain the auto-increment Id for
    ///   CodeData.BatchId association), and the four counters are written back when the import ends. Even if this run
    ///   yields 0 valid rows (all duplicates), the ledger record is kept for traceability.
    ///
    /// [Counter convention] TotalRows = ValidCount + DuplicateCount + InvalidCount
    ///   ValidCount     : rows actually inserted into the DB
    ///   DuplicateCount : duplicates within the file + already present in the DB, combined
    ///   InvalidCount   : empty rows + rows containing Chinese characters, combined
    /// </summary>
    public class ImportBatch
    {
        /// <summary>Primary key, auto-increment</summary>
        public long Id { get; set; } = 0;

        /// <summary>Batch number, format yyyyMMddHHmmssfff; a unique index exists in the DB</summary>
        public string BatchNo { get; set; } = string.Empty;

        /// <summary>Source type: Txt / Excel</summary>
        public ImportSourceType SourceType { get; set; } = ImportSourceType.Txt;

        /// <summary>Source file name (without path)</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>Full path of the source file</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Total number of rows in the source file (including empty and duplicate rows)</summary>
        public int TotalRows { get; set; } = 0;

        /// <summary>Number of rows validly inserted</summary>
        public int ValidCount { get; set; } = 0;

        /// <summary>Number of duplicate rows (duplicates within the file + already present in the DB)</summary>
        public int DuplicateCount { get; set; } = 0;

        /// <summary>Number of invalid rows (empty rows + rows containing Chinese characters)</summary>
        public int InvalidCount { get; set; } = 0;

        /// <summary>Import time, format yyyy-MM-dd HH:mm:ss</summary>
        public string ImportTime { get; set; } = string.Empty;

        /// <summary>Operator, defaults to the current Windows login user name</summary>
        public string Operator { get; set; } = string.Empty;

        /// <summary>Remark, for example "user cancelled the import"</summary>
        public string Remark { get; set; } = string.Empty;
    }
}

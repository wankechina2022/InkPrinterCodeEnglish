namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] Code data entity -- maps to the SQLite table CodeData; one record = one code pending or already printed
    ///
    /// [Fields map one-to-one to the table] Property names are kept exactly identical to the column names, which makes
    ///   the hand-written DAL mapping easy to cross-check.
    ///
    /// [Why time is a string] SQLite has no native datetime type; this project uniformly stores TEXT in
    ///   "yyyy-MM-dd HH:mm:ss" format -- the lexicographic order of that format equals chronological order, so it can
    ///   be used directly with BETWEEN for range filtering, avoiding format ambiguity and DateTime.MinValue dirty
    ///   values from converting back and forth between DateTime and TEXT. Use Extensions.ToDateTimeOrDefault when a
    ///   DateTime is needed, and Extensions.ToDbTimeString when writing to the DB.
    ///
    /// [Defaults] All properties have defaults (development convention: every declared variable must have a default),
    ///   and strings default to string.Empty rather than null, so callers do not have to null-check everywhere.
    /// </summary>
    public class CodeData
    {
        /// <summary>Primary key, auto-increment. Delete operations always go by this primary key</summary>
        public long Id { get; set; } = 0;

        /// <summary>Owning import batch Id (matches ImportBatch.Id); 0 means no associated batch</summary>
        public long BatchId { get; set; } = 0;

        /// <summary>Row number in the source file (starting from 1), used to trace back to the original file position when investigating import issues</summary>
        public int RowNo { get; set; } = 0;

        /// <summary>Code value. Globally unique (a UNIQUE index exists in the DB); import only Trims it and imposes no length or character-set restriction</summary>
        public string CodeValue { get; set; } = string.Empty;

        /// <summary>Print status, NotPrinted by default</summary>
        public PrintStatus PrintStatus { get; set; } = PrintStatus.NotPrinted;

        /// <summary>Time sent to the inkjet printer, format yyyy-MM-dd HH:mm:ss; empty string when not sent (populated during printing)</summary>
        public string SendTime { get; set; } = string.Empty;

        /// <summary>Print completion time (the moment a success response is received from the printer); empty string when not completed (populated during printing)</summary>
        public string PrintTime { get; set; } = string.Empty;

        /// <summary>Raw text returned by the printer; the original message is kept on failure to aid troubleshooting (populated during printing)</summary>
        public string FeedbackText { get; set; } = string.Empty;

        /// <summary>Retry count, incremented on failed retries (maintained by the printing flow)</summary>
        public int RetryCount { get; set; } = 0;

        /// <summary>Insert time (that is, import time), format yyyy-MM-dd HH:mm:ss. The time range filter on the data view page uses this column</summary>
        public string CreateTime { get; set; } = string.Empty;
    }
}

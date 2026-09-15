namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] Result of a batch delete -- returned by the BLL to the UI for a result message
    ///
    /// [Why three separate numbers] Deletion is performed in batch according to "the current filter conditions",
    ///   so the user cannot see exactly which rows were removed. The result must therefore state three things
    ///   clearly, otherwise a mistaken deletion would go unnoticed:
    ///   FilteredCount  how many rows the current filter matched in total (the scope the user sees)
    ///   DeletedCount   how many rows were actually deleted
    ///   SkippedCount   how many rows were blocked because "NotPrinted status must not be deleted"
    ///
    /// [Identity] FilteredCount = DeletedCount + SkippedCount
    ///   (on failure DeletedCount=0 and SkippedCount=FilteredCount, with the reason explained in Message)
    ///
    /// [Business constraint (adjusted 2026-09-10)] On the default path, codes in NotPrinted (PrintStatus=0) may not
    ///   be deleted -- the DAL SQL hard-codes PrintStatus &lt;&gt; 0 as a fallback, so even when the UI layer fails to
    ///   block it, the database layer still does. But all imported codes are in NotPrinted status, and a pure hard
    ///   rule creates the deadlock of "mistakenly imported data that cannot be deleted", so a separate forced
    ///   cleanup channel (AllowNotPrinted=true) exists, effective only after the user explicitly checks a box and
    ///   confirms a second time.
    /// </summary>
    public class DeleteResult
    {
        /// <summary>Whether the operation succeeded</summary>
        public bool Success { get; set; } = false;

        /// <summary>Result explanation / failure reason (user-facing friendly text)</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Total rows matched by the current filter</summary>
        public int FilteredCount { get; set; } = 0;

        /// <summary>Rows actually deleted</summary>
        public int DeletedCount { get; set; } = 0;

        /// <summary>Rows blocked and not deleted because of NotPrinted status</summary>
        public int SkippedCount { get; set; } = 0;

        /// <summary>
        /// [2026-09-10] Whether this delete went through the "include NotPrinted" forced cleanup channel.
        /// true = NotPrinted data was deleted as well (only possible after checking "include NotPrinted"), in which
        /// case SkippedCount is always 0.
        /// </summary>
        public bool AllowNotPrinted { get; set; } = false;
    }
}

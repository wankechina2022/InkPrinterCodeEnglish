namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] Query condition object for the data view page
    ///
    /// [Filter scope] As confirmed: only the two conditions "print status" + "time range" are kept;
    ///   no batch filter and no fuzzy code value search.
    ///
    /// [Why bool switches instead of nullable types] Using the two explicit switches HasStatusFilter /
    ///   HasTimeFilter is more direct than a nullable form like PrintStatus? -- when the DAL builds the WHERE
    ///   clause it is immediately obvious whether a condition is applied, and it avoids "value 0 (NotPrinted)"
    ///   being mistaken for "no value supplied".
    ///
    /// [Time format] StartTime / EndTime must be "yyyy-MM-dd HH:mm:ss" strings, exactly matching the storage
    ///   format of the CreateTime column so they can take part directly in BETWEEN comparisons.
    ///   They are produced by the UI layer's DateTimePicker via Extensions.ToDbTimeString; the convention is that
    ///   the start time is 00:00:00 of the day and the end time is 23:59:59 of the day.
    ///
    /// [Paging boundaries] PageIndex starts at 1; PageSize comes from the PageSize configuration item (default 100).
    ///   The export feature reuses the same filter object but ignores the paging fields (it exports the full result
    ///   set for the current filter).
    /// </summary>
    public class CodeQueryFilter
    {
        /// <summary>Whether status filtering is enabled. false = query all statuses</summary>
        public bool HasStatusFilter { get; set; } = false;

        /// <summary>Status filter value, only effective when HasStatusFilter = true</summary>
        public PrintStatus Status { get; set; } = PrintStatus.NotPrinted;

        /// <summary>Whether time range filtering is enabled. false = no time restriction</summary>
        public bool HasTimeFilter { get; set; } = false;

        /// <summary>Start time (inclusive), format yyyy-MM-dd HH:mm:ss</summary>
        public string StartTime { get; set; } = string.Empty;

        /// <summary>End time (inclusive), format yyyy-MM-dd HH:mm:ss</summary>
        public string EndTime { get; set; } = string.Empty;

        /// <summary>Current page number, starting from 1</summary>
        public int PageIndex { get; set; } = 1;

        /// <summary>Rows per page</summary>
        public int PageSize { get; set; } = 100;

        /// <summary>
        /// [2026-09-10] Whether a delete may also remove data in "NotPrinted" status (default false = not allowed)
        ///
        /// [Why this switch exists]
        ///   All imported codes are in "NotPrinted" status. Previously "never delete NotPrinted" was a hard rule,
        ///   but it created a deadlock: if wrong data was imported (typically the Excel code value column was not
        ///   set to Text format, so long codes over 15 digits lost their trailing digits inside Excel), that dirty
        ///   data could neither be deleted nor stop occupying the unique index, and re-importing the correct codes
        ///   would still be flagged as duplicates -- the whole batch was completely blocked.
        ///   So a "forced cleanup after confirmation" channel must exist.
        ///
        /// [Safety design] Off by default. It is only set to true when "include NotPrinted" is explicitly checked
        ///   on the UI, and checking it still requires a second strong confirmation box (see
        ///   DataViewForm.btnDelete_Click). When unchecked, behaviour is exactly as before: not a single NotPrinted
        ///   row is deleted.
        ///
        /// [Corresponding DAL behaviour]
        ///   true  -> neither the count nor the delete appends PrintStatus &lt;&gt; 0, so NotPrinted data is deleted as well;
        ///   false -> both the count and the delete append PrintStatus &lt;&gt; 0, so NotPrinted data is excluded throughout.
        /// </summary>
        public bool AllowDeleteNotPrinted { get; set; } = false;
    }
}

namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] Print status enumeration for code data
    ///
    /// [Value convention] The numbers map one-to-one to the CodeData.PrintStatus column in the database; the int value is stored:
    ///   0 = NotPrinted : the initial state after import; the printing flow takes this data in Id order
    ///   1 = Printed    : [2026-09-10 code-taking occupancy model] the moment a code is taken from the DB it is
    ///                    marked with this status (occupancy), and only afterwards written to the printer; a failed
    ///                    write is not rolled back either (better to miss a print than to print twice)
    ///   2 = Failed     : sending or printing failed (the retry count is recorded in RetryCount)
    ///   3 = Voided     : a code manually judged as no longer used
    ///
    /// [Boundary] Once the values go live they must not be changed -- historical data in the DB is stored with this
    ///         numbering; new statuses may only be appended (4, 5, ...), and existing values must not be reused or adjusted.
    ///
    /// [Business constraint] The delete feature on the data view page forbids deleting records in NotPrinted (0)
    ///             status; NotPrinted codes are pending production material, and deleting them by mistake would
    ///             directly cause missing production data.
    /// </summary>
    public enum PrintStatus
    {
        /// <summary>NotPrinted (0) -- the initial state after import</summary>
        NotPrinted = 0,

        /// <summary>
        /// Printed (1) -- [2026-09-10 code-taking occupancy model] marked with this status (occupancy lock) the
        /// moment it is taken from the database. The take condition is always PrintStatus = 0, so the same code can
        /// never be taken out twice, eliminating duplicate printing at the root.
        /// A failed write to the printer is not rolled back either (plan A: better to miss a print than to print
        /// twice); each code is written only once, with no retry.
        /// </summary>
        Printed = 1,

        /// <summary>Failed (2) -- sending or printing failed</summary>
        Failed = 2,

        /// <summary>Voided (3) -- manually judged as no longer used</summary>
        Voided = 3
    }
}

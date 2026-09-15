using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] Data access class for the CodeData table
    ///
    /// [Development rules implemented here]
    ///   1. No SELECT *: every query lists its column names explicitly, so adding a column later cannot disrupt the UI or exports;
    ///   2. Everything is parameterised: code values may contain quotes or semicolons, and string-concatenated SQL would inevitably break;
    ///   3. DELETE must always carry a condition: deletion always first queries the primary keys matching the filter and then deletes them in batches by key,
    ///      and every DELETE additionally carries a PrintStatus &lt;&gt; 0 safety condition (see DeleteByFilter);
    ///   4. Ordering must be explicit: paged queries always use ORDER BY Id; paging without ORDER BY has no guaranteed order under SQLite,
    ///      which produces the odd effect of the same row appearing on two different pages.
    ///
    /// [Why paged queries return List&lt;CodeData&gt; instead of DataTable]
    ///   The UI only displays data, so an entity list is more straightforward; only export needs a DataTable (column names are the headers), which has its own QueryForExport path.
    /// </summary>
    public static class CodeDataDAL
    {
        /// <summary>Query columns. All queries use this same string so column sets never diverge</summary>
        private const string COLUMNS = "Id, BatchId, RowNo, CodeValue, PrintStatus, SendTime, PrintTime, FeedbackText, RetryCount, CreateTime";

        private const string SQL_INSERT = @"
INSERT INTO CodeData
    (BatchId, RowNo, CodeValue, PrintStatus, SendTime, PrintTime, FeedbackText, RetryCount, CreateTime)
VALUES
    (@BatchId, @RowNo, @CodeValue, @PrintStatus, @SendTime, @PrintTime, @FeedbackText, @RetryCount, @CreateTime);";

        // ============================================================
        // 1. Statistics
        // ============================================================

        /// <summary>Get the total number of rows in the table</summary>
        public static long GetTotalCount()
        {
            object? value = SqliteHelper.ExecuteScalar("SELECT COUNT(1) FROM CodeData;");
            return value.ToLong(0);
        }

        /// <summary>
        /// Get the count for a given status (used by the home dashboard)
        /// [Index] Uses IX_CodeData_Status_Id, so even 100k rows return in milliseconds.
        /// </summary>
        public static int GetStatusCount(PrintStatus status)
        {
            SqliteParameter[] parameters = new SqliteParameter[]
            {
                SqliteHelper.CreateParameter("@PrintStatus", (int)status)
            };

            object? value = SqliteHelper.ExecuteScalar(
                "SELECT COUNT(1) FROM CodeData WHERE PrintStatus = @PrintStatus;", parameters);

            return value.ToInt(0);
        }

        // ============================================================
        // 1.5 Code claiming and status write-back for printing (added in round 3 of phase two)
        // ============================================================

        /// <summary>
        /// Take N not-printed codes in Id order (Mr. Wan's requirement: claim codes from the data in Id order)
        /// [Index] It uses IX_CodeData_Status_Id, so with no codes available it returns an empty table in milliseconds.
        /// [Return] Guaranteed non-null: with no codes available it returns an empty list, and the caller can just check Count.
        /// </summary>
        public static List<CodeData> GetNotPrintedCodes(int limit)
        {
            List<CodeData> list = new List<CodeData>();

            if (limit <= 0)
            {
                return list;
            }

            string sql = "SELECT " + COLUMNS + " FROM CodeData WHERE PrintStatus = @PrintStatus"
                         + " ORDER BY Id ASC LIMIT @Limit;";

            SqliteParameter[] parameters = new SqliteParameter[]
            {
                SqliteHelper.CreateParameter("@PrintStatus", (int)PrintStatus.NotPrinted),
                SqliteHelper.CreateParameter("@Limit", limit)
            };

            DataTable table = SqliteHelper.ExecuteQuery(sql, parameters);
            return ToCodeDataList(table);
        }

        /// <summary>
        /// Write a code back as printed (code-claim occupancy model: called immediately when the code is taken, acting as an occupancy lock)
        ///
        /// [Occupancy semantics] A successful mark means this code is exclusively owned by this send operation; only then is it written to the printer, and a failed write does not roll the mark back.
        /// [Duplicate protection] The WHERE clause includes PrintStatus = 0 -- only codes never marked before can be marked;
        ///   a return of 0 means the code was already marked by another path, and the caller must abandon the send (never perform a second write).
        /// </summary>
        /// <param name="id">Code primary key</param>
        /// <param name="sendTime">Write time (yyyy-MM-dd HH:mm:ss)</param>
        /// <param name="feedbackText">Feedback note (empty string by default in write mode)</param>
        /// <returns>Number of affected rows (0 = the code is no longer in the not-printed state; nothing was changed)</returns>
        public static int MarkPrinted(long id, string sendTime, string feedbackText)
        {
            string sql = @"UPDATE CodeData
SET PrintStatus = @PrintStatus, SendTime = @SendTime, FeedbackText = @FeedbackText
WHERE Id = @Id AND PrintStatus = 0;";

            SqliteParameter[] parameters = new SqliteParameter[]
            {
                SqliteHelper.CreateParameter("@PrintStatus", (int)PrintStatus.Printed),
                SqliteHelper.CreateParameter("@SendTime", sendTime ?? string.Empty),
                SqliteHelper.CreateParameter("@FeedbackText", feedbackText ?? string.Empty),
                SqliteHelper.CreateParameter("@Id", id)
            };

            return SqliteHelper.ExecuteNonQuery(sql, parameters);
        }

        /// <summary>
        /// Increment the retry count by 1.
        /// [2026-09-10] Orphaned-method annotation: under the code-claim occupancy model, "a code is written only once
        ///   and never retried" has been finalized (Mr. Wan's 17:29 instruction), so this method currently has no call
        ///   site and is retained for future use (should a feature such as "manually re-push a failed code" be introduced).
        ///   The counter column RetryCount remains in the table but is no longer written by the code-sending flow.
        /// </summary>
        public static int IncrementRetry(long id)
        {
            string sql = "UPDATE CodeData SET RetryCount = RetryCount + 1 WHERE Id = @Id;";

            SqliteParameter[] parameters = new SqliteParameter[]
            {
                SqliteHelper.CreateParameter("@Id", id)
            };

            return SqliteHelper.ExecuteNonQuery(sql, parameters);
        }

        // ============================================================
        // 2. Duplicate-detection support (for import)
        // ============================================================

        /// <summary>
        /// Get all code values in the database
        /// [Purpose] The "in-memory HashSet path" of the two-path duplicate detection -- when the total volume is small, reading everything in one go is the fastest option.
        /// [Memory estimate] 100k rows x about 20 characters is roughly 6 MB, which is manageable; above the threshold (the DedupHashSetThreshold setting)
        ///   ImportBLL switches to batched IN queries via GetExistingCodeValues and does not call this method.
        /// </summary>
        public static List<string> GetAllCodeValues()
        {
            List<string> values = new List<string>();

            DataTable table = SqliteHelper.ExecuteQuery("SELECT CodeValue FROM CodeData;");
            for (int i = 0; i < table.Rows.Count; i++)
            {
                values.Add(table.Rows[i]["CodeValue"].ToSafeString());
            }

            return values;
        }

        /// <summary>
        /// Batched IN query: pick out the candidate code values that already exist in the database
        ///
        /// [Why batching] SQLite limits a single statement to 999 parameters (fewer in older versions),
        ///   so 100k candidate codes cannot be packed into one IN clause and must be sliced by SqlInParamBatchSize (default 500).
        ///
        /// [Return value] The set of code values that already exist. The caller uses it to remove those entries from the candidates.
        /// </summary>
        public static HashSet<string> GetExistingCodeValues(List<string> candidates)
        {
            HashSet<string> existing = new HashSet<string>();

            if (candidates == null || candidates.Count <= 0)
            {
                return existing;
            }

            int batchSize = ConfigHelper.SqlInParamBatchSize;

            for (int startIndex = 0; startIndex < candidates.Count; startIndex += batchSize)
            {
                int count = batchSize;
                if (startIndex + count > candidates.Count)
                {
                    count = candidates.Count - startIndex;
                }

                // Build the parameter placeholders (@p0,@p1,...) —— placeholders are being concatenated, not values, so
                // it is safe with no injection risk
                System.Text.StringBuilder placeholders = new System.Text.StringBuilder();
                SqliteParameter[] parameters = new SqliteParameter[count];

                for (int i = 0; i < count; i++)
                {
                    if (i > 0)
                    {
                        placeholders.Append(",");
                    }
                    string paramName = "@p" + i.ToString();
                    placeholders.Append(paramName);
                    parameters[i] = SqliteHelper.CreateParameter(paramName, candidates[startIndex + i]);
                }

                string sql = "SELECT CodeValue FROM CodeData WHERE CodeValue IN (" + placeholders.ToString() + ");";
                DataTable table = SqliteHelper.ExecuteQuery(sql, parameters);

                for (int r = 0; r < table.Rows.Count; r++)
                {
                    existing.Add(table.Rows[r]["CodeValue"].ToSafeString());
                }
            }

            return existing;
        }

        // ============================================================
        // 3. Batch insert (for import; must run inside a transaction)
        // ============================================================

        /// <summary>
        /// Batch-insert code data inside a transaction
        ///
        /// [Mechanism] The SQL is compiled once, then a loop repeatedly resets the parameter values and calls ExecuteNonQuery.
        ///   100k rows finish in 1-3 seconds; creating a new SqliteCommand for every row would multiply that time several times over.
        ///
        /// [Cancellation] The cancel token is checked before each batch; returning false means the user cancelled,
        ///   and the caller (BLL) is responsible for rolling the transaction back -- a half-imported state is never acceptable.
        /// </summary>
        /// <returns>Number of rows actually inserted; on cancellation, the number inserted so far (which the transaction then rolls back, so effectively nothing was inserted)</returns>
        public static int InsertBatch(SqliteConnection connection, SqliteTransaction transaction,
            List<CodeData> dataList, Func<bool>? isCanceled)
        {
            if (dataList == null || dataList.Count <= 0)
            {
                return 0;
            }

            int inserted = 0;
            int batchSize = ConfigHelper.ImportBatchSize;

            using (SqliteCommand command = SqliteHelper.CreateCommand(connection, transaction, SQL_INSERT))
            {
                command.Parameters.Add(SqliteHelper.CreateParameter("@BatchId", 0L));
                command.Parameters.Add(SqliteHelper.CreateParameter("@RowNo", 0));
                command.Parameters.Add(SqliteHelper.CreateParameter("@CodeValue", string.Empty));
                command.Parameters.Add(SqliteHelper.CreateParameter("@PrintStatus", 0));
                command.Parameters.Add(SqliteHelper.CreateParameter("@SendTime", string.Empty));
                command.Parameters.Add(SqliteHelper.CreateParameter("@PrintTime", string.Empty));
                command.Parameters.Add(SqliteHelper.CreateParameter("@FeedbackText", string.Empty));
                command.Parameters.Add(SqliteHelper.CreateParameter("@RetryCount", 0));
                command.Parameters.Add(SqliteHelper.CreateParameter("@CreateTime", string.Empty));

                for (int i = 0; i < dataList.Count; i++)
                {
                    if (i % batchSize == 0 && isCanceled != null && isCanceled())
                    {
                        return inserted;
                    }

                    CodeData item = dataList[i];

                    command.Parameters["@BatchId"].Value = item.BatchId;
                    command.Parameters["@RowNo"].Value = item.RowNo;
                    command.Parameters["@CodeValue"].Value = item.CodeValue;
                    command.Parameters["@PrintStatus"].Value = (int)item.PrintStatus;
                    command.Parameters["@SendTime"].Value = item.SendTime;
                    command.Parameters["@PrintTime"].Value = item.PrintTime;
                    command.Parameters["@FeedbackText"].Value = item.FeedbackText;
                    command.Parameters["@RetryCount"].Value = item.RetryCount;
                    command.Parameters["@CreateTime"].Value = item.CreateTime;

                    command.ExecuteNonQuery();
                    inserted++;
                }
            }

            return inserted;
        }

        // ============================================================
        // 4. Paged query (used by the data view page)
        // ============================================================

        /// <summary>
        /// Paged query by filter
        /// </summary>
        /// <param name="filter">Filter criteria (including the paging fields)</param>
        /// <param name="totalCount">Output: total number of matching rows (not affected by paging)</param>
        /// <returns>List of rows on the current page</returns>
        public static List<CodeData> QueryByFilter(CodeQueryFilter filter, out int totalCount)
        {
            totalCount = 0;

            if (filter == null)
            {
                return new List<CodeData>();
            }

            List<SqliteParameter> parameters = new List<SqliteParameter>();
            string whereClause = BuildWhereClause(filter, false, parameters);

            totalCount = GetCountByWhere(whereClause, parameters);

            if (totalCount <= 0)
            {
                return new List<CodeData>();
            }

            // Page-number out-of-range protection: for instance after deleting data while sitting on the last page,
            // automatically fall back to the last page
            int pageSize = filter.PageSize;
            if (pageSize <= 0)
            {
                pageSize = ConfigHelper.PageSize;
            }

            int totalPages = (totalCount + pageSize - 1) / pageSize;
            int pageIndex = filter.PageIndex;
            if (pageIndex < 1)
            {
                pageIndex = 1;
            }
            if (pageIndex > totalPages)
            {
                pageIndex = totalPages;
            }

            int offset = (pageIndex - 1) * pageSize;

            string sql = "SELECT " + COLUMNS + " FROM CodeData " + whereClause
                         + " ORDER BY Id ASC LIMIT @PageSize OFFSET @Offset;";

            List<SqliteParameter> pageParameters = new List<SqliteParameter>(parameters);
            pageParameters.Add(SqliteHelper.CreateParameter("@PageSize", pageSize));
            pageParameters.Add(SqliteHelper.CreateParameter("@Offset", offset));

            DataTable table = SqliteHelper.ExecuteQuery(sql, pageParameters.ToArray());
            return ToCodeDataList(table);
        }

        /// <summary>
        /// Query all matching data by filter (for export; paging is ignored)
        /// [Export columns] Only Id / code value / status / import time are exported -- the send, print and feedback columns introduced in stage two are always empty for now,
        ///   and exporting a pile of empty columns only gets in the way; they can be added later once stage two goes live.
        /// </summary>
        public static DataTable QueryForExport(CodeQueryFilter filter)
        {
            DataTable result = new DataTable();
            result.Columns.Add("No.", typeof(long));
            result.Columns.Add("Code Value", typeof(string));
            result.Columns.Add("Status", typeof(string));
            result.Columns.Add("Import Time", typeof(string));

            if (filter == null)
            {
                return result;
            }

            List<SqliteParameter> parameters = new List<SqliteParameter>();
            string whereClause = BuildWhereClause(filter, false, parameters);

            string sql = "SELECT " + COLUMNS + " FROM CodeData " + whereClause + " ORDER BY Id ASC;";

            DataTable table = SqliteHelper.ExecuteQuery(sql, parameters.ToArray());

            for (int i = 0; i < table.Rows.Count; i++)
            {
                DataRow sourceRow = table.Rows[i];
                DataRow newRow = result.NewRow();

                newRow["No."] = sourceRow["Id"].ToLong(0);
                newRow["Code Value"] = sourceRow["CodeValue"].ToSafeString();
                newRow["Status"] = EnumHelper.GetPrintStatusText(sourceRow["PrintStatus"].ToInt(0));
                newRow["Import Time"] = sourceRow["CreateTime"].ToSafeString();

                result.Rows.Add(newRow);
            }

            return result;
        }

        // ============================================================
        // 5. Deletion (used by the data view page)
        // ============================================================

        /// <summary>
        /// Count the total number of records matched by the current filter (not excluding not-printed ones)
        /// [Purpose] The delete result has to tell the user "N matched, M deleted, K skipped", and this N is what it computes.
        /// </summary>
        public static int GetCountByFilter(CodeQueryFilter filter)
        {
            if (filter == null)
            {
                return 0;
            }

            List<SqliteParameter> parameters = new List<SqliteParameter>();
            string whereClause = BuildWhereClause(filter, false, parameters);

            return GetCountByWhere(whereClause, parameters);
        }

        /// <summary>
        /// Count the deletable rows under the current filter
        /// [Purpose] Called before deletion to compute the expected count shown in the user confirmation; the number displayed must match reality.
        ///
        /// [Two paths] filter.AllowDeleteNotPrinted decides whether not-printed rows are excluded:
        ///   false (default) -> append PrintStatus &lt;&gt; 0, so the count covers only the rows that may be deleted;
        ///   true (the "include not printed" checkbox) -> nothing is appended, so the count covers everything the filter matched.
        /// </summary>
        public static int GetDeleteCount(CodeQueryFilter filter)
        {
            if (filter == null)
            {
                return 0;
            }

            List<SqliteParameter> parameters = new List<SqliteParameter>();
            // Only append the exclusion condition when "include not printed" is unchecked
            bool excludeNotPrinted = !filter.AllowDeleteNotPrinted;
            string whereClause = BuildWhereClause(filter, excludeNotPrinted, parameters);

            return GetCountByWhere(whereClause, parameters);
        }

        /// <summary>
        /// Bulk delete according to the current filter
        ///
        /// [Dual path (the "include not printed" forced-delete channel added on 2026-09-10)]
        ///   filter.AllowDeleteNotPrinted = false (default) → not-printed records are never deleted, exactly as before;
        ///   filter.AllowDeleteNotPrinted = true            → not-printed data is deleted along with the rest, for
        ///     cleaning up dirty data from a bad import.
        ///
        /// [Why this opening is mandatory]
        ///   All imported data is in the not-printed state. If "not printed cannot be deleted" were the only rule, then
        ///   once wrong data has been imported (the most typical case being an Excel code-value column not set to the
        ///   text format, so long codes lose their trailing digits), that batch can neither be deleted nor stop occupying
        ///   the CodeValue unique index, so re-importing the correct data would still be judged a duplicate, forming a
        ///   deadlock.
        ///
        /// [Two steps (standard: DELETE must carry a condition and should use the primary key)]
        ///   Step one: query the primary key Id of every record to be deleted according to the condition;
        ///   Step two: DELETE by primary key in batches.
        ///   On the default path, both step one's WHERE and step two's statement carry the PrintStatus &lt;&gt; 0 guard
        ///   again —— even if the status were changed by another path between the query and the delete, no not-printed
        ///   code would be deleted.
        ///   On the forced path this guard is not added, but the DELETE still carries the primary-key IN condition,
        ///   complying with the "DELETE must carry a condition" red line.
        ///
        /// [Transaction] The whole process runs in one transaction; a failure midway rolls everything back, so there is
        /// no half-deleted state.
        ///
        /// [Boundaries] When the deletable count is 0 it returns 0 directly, opening no transaction and writing no log.
        /// </summary>
        /// <returns>The number of records actually deleted</returns>
        public static int DeleteByFilter(CodeQueryFilter filter)
        {
            if (filter == null)
            {
                return 0;
            }

            bool excludeNotPrinted = !filter.AllowDeleteNotPrinted;

            List<SqliteParameter> parameters = new List<SqliteParameter>();
            string whereClause = BuildWhereClause(filter, excludeNotPrinted, parameters);

            // Not-printed guard condition: in force throughout the default path, not added on the forced-cleanup path
            string guardSql = excludeNotPrinted ? " AND PrintStatus <> 0" : string.Empty;

            int totalDeleted = 0;

            SqliteHelper.ExecuteInTransaction(delegate (SqliteConnection connection, SqliteTransaction transaction)
            {
                // Step one: query the primary keys
                List<long> idList = new List<long>();
                string selectSql = "SELECT Id FROM CodeData " + whereClause + ";";

                using (SqliteCommand selectCommand = SqliteHelper.CreateCommand(connection, transaction, selectSql))
                {
                    SqliteHelper.AddParameters(selectCommand, parameters.ToArray());

                    using (SqliteDataReader reader = selectCommand.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            idList.Add(reader.GetValue(0).ToLong(0));
                        }
                    }
                }

                if (idList.Count <= 0)
                {
                    return;
                }

                // Step 2: delete in batches by primary key
                int batchSize = ConfigHelper.SqlInParamBatchSize;

                for (int startIndex = 0; startIndex < idList.Count; startIndex += batchSize)
                {
                    int count = batchSize;
                    if (startIndex + count > idList.Count)
                    {
                        count = idList.Count - startIndex;
                    }

                    System.Text.StringBuilder placeholders = new System.Text.StringBuilder();
                    SqliteParameter[] deleteParameters = new SqliteParameter[count];

                    for (int i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            placeholders.Append(",");
                        }
                        string paramName = "@p" + i.ToString();
                        placeholders.Append(paramName);
                        deleteParameters[i] = SqliteHelper.CreateParameter(paramName, idList[startIndex + i]);
                    }

                    // Guard condition PrintStatus <> 0 (default path only): even if the status changes after the query,
                    // not-printed codes will never be deleted
                    string deleteSql = "DELETE FROM CodeData WHERE Id IN (" + placeholders.ToString() + ")" + guardSql + ";";

                    using (SqliteCommand deleteCommand = SqliteHelper.CreateCommand(connection, transaction, deleteSql))
                    {
                        SqliteHelper.AddParameters(deleteCommand, deleteParameters);
                        totalDeleted += deleteCommand.ExecuteNonQuery();
                    }
                }
            });

            return totalDeleted;
        }

        // ============================================================
        // Private helpers
        // ============================================================

        /// <summary>
        /// Build the WHERE clause
        /// [When there are no conditions] Returns "WHERE 1=1"; later conditions always start with " AND ...", so callers never have to decide whether to add AND.
        /// </summary>
        /// <param name="filter">Filter criteria</param>
        /// <param name="excludeNotPrinted">Whether to append the "exclude not printed" condition (used by the delete scenario)</param>
        /// <param name="parameters">Collection that receives the parameters</param>
        private static string BuildWhereClause(CodeQueryFilter filter, bool excludeNotPrinted, List<SqliteParameter> parameters)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("WHERE 1=1");

            if (filter.HasStatusFilter)
            {
                builder.Append(" AND PrintStatus = @PrintStatus");
                parameters.Add(SqliteHelper.CreateParameter("@PrintStatus", (int)filter.Status));
            }

            if (filter.HasTimeFilter)
            {
                // CreateTime is stored as "yyyy-MM-dd HH:mm:ss", whose lexicographic order equals chronological order, so BETWEEN works directly
                builder.Append(" AND CreateTime >= @StartTime AND CreateTime <= @EndTime");
                parameters.Add(SqliteHelper.CreateParameter("@StartTime", filter.StartTime));
                parameters.Add(SqliteHelper.CreateParameter("@EndTime", filter.EndTime));
            }

            if (excludeNotPrinted)
            {
                builder.Append(" AND PrintStatus <> 0");
            }

            return builder.ToString();
        }

        /// <summary>Count records by the given WHERE clause</summary>
        private static int GetCountByWhere(string whereClause, List<SqliteParameter> parameters)
        {
            string sql = "SELECT COUNT(1) FROM CodeData " + whereClause + ";";
            object? value = SqliteHelper.ExecuteScalar(sql, parameters.ToArray());
            return value.ToInt(0);
        }

        /// <summary>DataTable → entity list (hand-written mapping, no reflection; see the Extensions class comment)</summary>
        private static List<CodeData> ToCodeDataList(DataTable table)
        {
            List<CodeData> list = new List<CodeData>();

            for (int i = 0; i < table.Rows.Count; i++)
            {
                DataRow row = table.Rows[i];

                CodeData item = new CodeData();
                item.Id = row["Id"].ToLong(0);
                item.BatchId = row["BatchId"].ToLong(0);
                item.RowNo = row["RowNo"].ToInt(0);
                item.CodeValue = row["CodeValue"].ToSafeString();
                item.PrintStatus = EnumHelper.ParsePrintStatus(row["PrintStatus"].ToInt(0));
                item.SendTime = row["SendTime"].ToSafeString();
                item.PrintTime = row["PrintTime"].ToSafeString();
                item.FeedbackText = row["FeedbackText"].ToSafeString();
                item.RetryCount = row["RetryCount"].ToInt(0);
                item.CreateTime = row["CreateTime"].ToSafeString();

                list.Add(item);
            }

            return list;
        }
    }
}

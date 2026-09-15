using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] Data access class for the ImportBatch ledger table
    ///
    /// [Purpose] Purely back-end traceability: records the file source and four counters of every import. The UI does not expose the batch concept,
    ///   but when something goes wrong it must be possible to find out which file this batch of codes came from, when it was imported and by whom.
    ///
    /// [When rows are written (all done by ImportBLL inside the same transaction)]
    ///   1. Import start: Insert -> take the auto-increment Id for use as CodeData.BatchId;
    ///   2. Import end: UpdateCounters -> write back the four counters (total / valid / duplicate / invalid).
    ///
    /// [Convention] This class never performs any table-structure change and offers no delete method -- the ledger is audit evidence and deletion is not offered.
    /// </summary>
    public static class ImportBatchDAL
    {
        /// <summary>Insert column names. Listed explicitly; no SELECT *</summary>
        private const string SQL_INSERT = @"
INSERT INTO ImportBatch
    (BatchNo, SourceType, FileName, FilePath, TotalRows, ValidCount, DuplicateCount, InvalidCount, ImportTime, Operator, Remark)
VALUES
    (@BatchNo, @SourceType, @FileName, @FilePath, @TotalRows, @ValidCount, @DuplicateCount, @InvalidCount, @ImportTime, @Operator, @Remark);";

        /// <summary>Write back the four counters. Only the counter columns are updated; the rest are untouched</summary>
        private const string SQL_UPDATE_COUNTERS = @"
UPDATE ImportBatch
   SET TotalRows      = @TotalRows,
       ValidCount     = @ValidCount,
       DuplicateCount = @DuplicateCount,
       InvalidCount   = @InvalidCount,
       Remark         = @Remark
 WHERE Id = @Id;";

        // ============================================================
        // Writes
        // ============================================================

        /// <summary>
        /// Insert one ledger row inside the given transaction and return the auto-increment primary key
        ///
        /// [Mechanism] The INSERT and SELECT last_insert_rowid() must share the same connection --
        ///   SQLite isolates last_insert_rowid per connection, so querying it on another connection returns 0.
        ///
        /// [Boundary] BatchNo has a unique index, so a collision (importing twice in the same millisecond) throws an exception,
        ///   and the caller (the BLL transaction's catch) rolls back the whole import so no half batch can exist.
        /// </summary>
        public static long Insert(SqliteConnection connection, SqliteTransaction transaction, ImportBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch), "The import batch entity cannot be null");
            }

            using (SqliteCommand command = SqliteHelper.CreateCommand(connection, transaction, SQL_INSERT))
            {
                command.Parameters.Add(SqliteHelper.CreateParameter("@BatchNo", batch.BatchNo));
                command.Parameters.Add(SqliteHelper.CreateParameter("@SourceType", (int)batch.SourceType));
                command.Parameters.Add(SqliteHelper.CreateParameter("@FileName", batch.FileName));
                command.Parameters.Add(SqliteHelper.CreateParameter("@FilePath", batch.FilePath));
                command.Parameters.Add(SqliteHelper.CreateParameter("@TotalRows", batch.TotalRows));
                command.Parameters.Add(SqliteHelper.CreateParameter("@ValidCount", batch.ValidCount));
                command.Parameters.Add(SqliteHelper.CreateParameter("@DuplicateCount", batch.DuplicateCount));
                command.Parameters.Add(SqliteHelper.CreateParameter("@InvalidCount", batch.InvalidCount));
                command.Parameters.Add(SqliteHelper.CreateParameter("@ImportTime", batch.ImportTime));
                command.Parameters.Add(SqliteHelper.CreateParameter("@Operator", batch.Operator));
                command.Parameters.Add(SqliteHelper.CreateParameter("@Remark", batch.Remark));

                command.ExecuteNonQuery();
            }

            // [2026-09-11] Fix for the InvalidOperationException thrown during import: while a connection is in a
            //   pending transaction, a command must be bound to that same transaction object to execute (the
            //   transaction-less overload SqliteHelper.ExecuteScalar cannot be used).
            //   Switched to CreateCommand bound to connection + transaction, consistent with the INSERT above;
            //   last_insert_rowid() is isolated per connection, so querying within the same connection and transaction
            //   yields an unchanged result.
            using (SqliteCommand idCommand = SqliteHelper.CreateCommand(connection, transaction, "SELECT last_insert_rowid();"))
            {
                object? idValue = idCommand.ExecuteScalar();
                return idValue.ToLong(0);
            }
        }

        /// <summary>
        /// Write back the four counters inside the specified transaction
        /// </summary>
        public static void UpdateCounters(SqliteConnection connection, SqliteTransaction transaction,
            long batchId, int totalRows, int validCount, int duplicateCount, int invalidCount, string? remark)
        {
            using (SqliteCommand command = SqliteHelper.CreateCommand(connection, transaction, SQL_UPDATE_COUNTERS))
            {
                command.Parameters.Add(SqliteHelper.CreateParameter("@Id", batchId));
                command.Parameters.Add(SqliteHelper.CreateParameter("@TotalRows", totalRows));
                command.Parameters.Add(SqliteHelper.CreateParameter("@ValidCount", validCount));
                command.Parameters.Add(SqliteHelper.CreateParameter("@DuplicateCount", duplicateCount));
                command.Parameters.Add(SqliteHelper.CreateParameter("@InvalidCount", invalidCount));

                string safeRemark = remark ?? string.Empty;
                command.Parameters.Add(SqliteHelper.CreateParameter("@Remark", safeRemark));

                command.ExecuteNonQuery();
            }
        }
    }
}

using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] 导入台账表 ImportBatch 的数据访问类
    ///
    /// 【用途】纯后台追溯：记录每次导入的文件来源与四项计数。界面不暴露批次概念，
    ///   但出问题时要能查"这批码是哪个文件、什么时候、谁导进来的"。
    ///
    /// 【写入时机（由 ImportBLL 在同一个事务内完成）】
    ///   1. 导入开始：Insert → 拿自增 Id 给 CodeData.BatchId 用；
    ///   2. 导入结束：UpdateCounters → 回写总行/有效/重复/无效四项计数。
    ///
    /// 【约定】本类不做"修改表结构"的任何动作，也不提供删除方法 —— 台账是审计凭据，不开放删除。
    /// </summary>
    public static class ImportBatchDAL
    {
        /// <summary>插入列名。明确列出，不用 SELECT *</summary>
        private const string SQL_INSERT = @"
INSERT INTO ImportBatch
    (BatchNo, SourceType, FileName, FilePath, TotalRows, ValidCount, DuplicateCount, InvalidCount, ImportTime, Operator, Remark)
VALUES
    (@BatchNo, @SourceType, @FileName, @FilePath, @TotalRows, @ValidCount, @DuplicateCount, @InvalidCount, @ImportTime, @Operator, @Remark);";

        /// <summary>回写四项计数。只更新计数列，其余列不动</summary>
        private const string SQL_UPDATE_COUNTERS = @"
UPDATE ImportBatch
   SET TotalRows      = @TotalRows,
       ValidCount     = @ValidCount,
       DuplicateCount = @DuplicateCount,
       InvalidCount   = @InvalidCount,
       Remark         = @Remark
 WHERE Id = @Id;";

        // ============================================================
        // 写入
        // ============================================================

        /// <summary>
        /// 在指定事务内插入一条台账，返回自增主键
        ///
        /// 【机制】INSERT 与 SELECT last_insert_rowid() 必须共用同一个连接 ——
        ///   SQLite 的 last_insert_rowid 是"按连接"隔离的，换个连接查会拿到 0。
        ///
        /// 【边界】BatchNo 有唯一索引，万一撞号（同一毫秒导两次）会抛异常，
        ///   由调用方（BLL 的事务 catch）回滚整个导入，不会出现半个批次。
        /// </summary>
        public static long Insert(SqliteConnection connection, SqliteTransaction transaction, ImportBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch), "导入批次实体不能为 null");
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

            // [2026-09-11] 修复导入报错 InvalidOperationException：连接处于挂起事务中时，
            //   命令必须绑定同一个事务对象才能执行（不能使用无事务重载的 SqliteHelper.ExecuteScalar）。
            //   改用 CreateCommand 绑定 connection + transaction，与上方 INSERT 保持一致；
            //   last_insert_rowid() 按连接隔离，同一连接同一事务内查询结果不变。
            using (SqliteCommand idCommand = SqliteHelper.CreateCommand(connection, transaction, "SELECT last_insert_rowid();"))
            {
                object? idValue = idCommand.ExecuteScalar();
                return idValue.ToLong(0);
            }
        }

        /// <summary>
        /// 在指定事务内回写四项计数
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

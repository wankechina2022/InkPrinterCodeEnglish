using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] 码数据表 CodeData 的数据访问类
    ///
    /// 【开发规约落实点】
    ///   1. 禁止 SELECT *：所有查询明确列出列名，避免以后加列把界面/导出撑乱；
    ///   2. 全部参数化：码值里可能带引号、分号，拼接 SQL 必出问题；
    ///   3. DELETE 必须带条件：删除一律"先按条件查出主键集合，再按主键分批删"，
    ///      且每条 DELETE 都再带一次 PrintStatus &lt;&gt; 0 的兜底条件（见 DeleteByFilter）；
    ///   4. 排序必须明确：分页查询固定 ORDER BY Id，没有 ORDER BY 的分页在 SQLite 下顺序不保证，
    ///      会出现同一条数据在两页里都出现的怪现象。
    ///
    /// 【分页查询为什么返回 List&lt;CodeData&gt; 而不是 DataTable】
    ///   界面只做展示，用实体列表更直白；导出才需要 DataTable（列名即表头），单独走 QueryForExport。
    /// </summary>
    public static class CodeDataDAL
    {
        /// <summary>查询列。所有查询统一用这一串，避免各处列不一致</summary>
        private const string COLUMNS = "Id, BatchId, RowNo, CodeValue, PrintStatus, SendTime, PrintTime, FeedbackText, RetryCount, CreateTime";

        private const string SQL_INSERT = @"
INSERT INTO CodeData
    (BatchId, RowNo, CodeValue, PrintStatus, SendTime, PrintTime, FeedbackText, RetryCount, CreateTime)
VALUES
    (@BatchId, @RowNo, @CodeValue, @PrintStatus, @SendTime, @PrintTime, @FeedbackText, @RetryCount, @CreateTime);";

        // ============================================================
        // 1. 统计
        // ============================================================

        /// <summary>取全表总条数</summary>
        public static long GetTotalCount()
        {
            object? value = SqliteHelper.ExecuteScalar("SELECT COUNT(1) FROM CodeData;");
            return value.ToLong(0);
        }

        /// <summary>
        /// 取指定状态的条数（主页看板用）
        /// 【索引】走 IX_CodeData_Status_Id，10 万级数据量也是毫秒级。
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
        // 1.5 喷码取码与状态回写（阶段二第 3 轮新增）
        // ============================================================

        /// <summary>
        /// 按 Id 顺序取 N 条未喷码（万总要求：从数据按 Id 顺序取码）
        /// 【索引】走 IX_CodeData_Status_Id，无码时毫秒级返回空表。
        /// 【返回】保证非空：无码时返回空列表，调用方判 Count 即可。
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
        /// 把一条码回写为已喷（取码占用制：取出码的瞬间立即调用，作为"占用锁"）
        ///
        /// 【占用语义】标记成功 = 本码归本次发送独占，之后才写入喷码机；写入失败也不回退。
        /// 【防重兜底】WHERE 带 PrintStatus = 0 —— 只有从未标记过的码能被标记；
        ///   返回 0 表示该码已被别的路径标记过，调用方必须放弃发送（绝不做二次写入）。
        /// </summary>
        /// <param name="id">码主键</param>
        /// <param name="sendTime">写入时间（yyyy-MM-dd HH:mm:ss）</param>
        /// <param name="feedbackText">反馈备注（写入制下默认空串）</param>
        /// <returns>受影响行数（0 = 该码已不是未喷状态，未做改动）</returns>
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
        /// 重试计数 +1。
        /// [2026-09-10] 孤立方法标注：取码占用制下"一个码只写一次、不重试"已封版（万总 17:29 指令），
        ///   本方法当前无任何调用点，保留备用（供将来若引入"失败码人工重推"之类功能时复用）。
        ///   计数器列 RetryCount 保留在表中，不再由发码流程写入。
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
        // 2. 去重支持（导入用）
        // ============================================================

        /// <summary>
        /// 取库中全部码值
        /// 【用途】去重双路径里的"内存 HashSet 路径" —— 库内总量不大时一次性全读进来比对，最快。
        /// 【内存估算】10 万条 × 约 20 字符 ≈ 6 MB，可控；超过阈值（配置 DedupHashSetThreshold）
        ///   时 ImportBLL 会改用 GetExistingCodeValues 分批 IN 查询，不会调用本方法。
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
        /// 分批 IN 查询：从候选码值里筛出库中已存在的那些
        ///
        /// 【为什么分批】SQLite 单条语句的参数上限是 999（旧版更低），
        ///   10 万个候选码不能一次性拼进 IN，必须按 SqlInParamBatchSize（默认 500）切片。
        ///
        /// 【返回值】已存在的码值集合。调用方据此把候选里的这批剔除掉。
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

                // 拼参数占位符（@p0,@p1,...）—— 拼的是占位符而不是值，安全无注入风险
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
        // 3. 批量插入（导入用，必须在事务内）
        // ============================================================

        /// <summary>
        /// 在事务内批量插入码数据
        ///
        /// 【机制】只编译一次 SQL，之后循环"重设参数值 → ExecuteNonQuery"。
        ///   10 万条数据 1~3 秒完成；如果每条都新建 SqliteCommand，耗时会翻好几倍。
        ///
        /// 【取消】每批开始前检查一次取消信号，返回 false 表示被用户取消，
        ///   调用方（BLL）负责让事务回滚 —— 绝不允许出现"导了一半"的数据。
        /// </summary>
        /// <returns>实际插入条数；被取消时返回已插入条数（随后由事务回滚，等于没插）</returns>
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
        // 4. 分页查询（数据查看页用）
        // ============================================================

        /// <summary>
        /// 按条件分页查询
        /// </summary>
        /// <param name="filter">筛选条件（含分页字段）</param>
        /// <param name="totalCount">输出：符合条件的总条数（不受分页影响）</param>
        /// <returns>当前页的数据列表</returns>
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

            // 页码越界保护：比如删完数据后停留在最后一页，自动回退到最后一页
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
        /// 按条件查询全量数据（导出用，忽略分页）
        /// 【导出列】只导 Id / 码值 / 状态 / 导入时间 —— 阶段二涉及的发送、喷印、反馈列当前恒为空，
        ///   导出一堆空列反而干扰查看；阶段二上线后再按需补充。
        /// </summary>
        public static DataTable QueryForExport(CodeQueryFilter filter)
        {
            DataTable result = new DataTable();
            result.Columns.Add("序号", typeof(long));
            result.Columns.Add("码值", typeof(string));
            result.Columns.Add("状态", typeof(string));
            result.Columns.Add("导入时间", typeof(string));

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

                newRow["序号"] = sourceRow["Id"].ToLong(0);
                newRow["码值"] = sourceRow["CodeValue"].ToSafeString();
                newRow["状态"] = EnumHelper.GetPrintStatusText(sourceRow["PrintStatus"].ToInt(0));
                newRow["导入时间"] = sourceRow["CreateTime"].ToSafeString();

                result.Rows.Add(newRow);
            }

            return result;
        }

        // ============================================================
        // 5. 删除（数据查看页用）
        // ============================================================

        /// <summary>
        /// 统计当前筛选条件命中的总条数（不排除未喷）
        /// 【用途】删除结果里要告诉用户"筛到 N 条、删了 M 条、跳过 K 条"，这个 N 就是它算的。
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
        /// 统计当前筛选条件下「可删除」的条数
        /// 【用途】删除前先用它算预计条数给用户确认，确认框里显示的数字必须与实际一致。
        ///
        /// 【双路径】按 filter.AllowDeleteNotPrinted 决定要不要排除未喷：
        ///   false（默认）→ 追加 PrintStatus &lt;&gt; 0，统计出的就是"能删的那部分"；
        ///   true（勾选"含未喷"）→ 不追加，统计出的就是筛选命中的全部。
        /// </summary>
        public static int GetDeleteCount(CodeQueryFilter filter)
        {
            if (filter == null)
            {
                return 0;
            }

            List<SqliteParameter> parameters = new List<SqliteParameter>();
            // 未勾"含未喷"时才追加排除条件
            bool excludeNotPrinted = !filter.AllowDeleteNotPrinted;
            string whereClause = BuildWhereClause(filter, excludeNotPrinted, parameters);

            return GetCountByWhere(whereClause, parameters);
        }

        /// <summary>
        /// 按当前筛选条件批量删除
        ///
        /// 【双路径（2026-09-10 新增"含未喷"强制删除通道）】
        ///   filter.AllowDeleteNotPrinted = false（默认）→ 未喷状态一律不删，行为与以前完全一致；
        ///   filter.AllowDeleteNotPrinted = true          → 未喷数据一并删除，用于清理导入错误的脏数据。
        ///
        /// 【为什么必须开这个口子】
        ///   导入的数据全是未喷状态。若"未喷不可删"是唯一规则，一旦导入了错数据
        ///   （Excel 码值列没设文本格式导致长码丢尾数是最典型的场景），
        ///   这批数据既删不掉、又占着 CodeValue 唯一索引，正确数据再导还会被判重复，形成死锁。
        ///
        /// 【两步走（规约：DELETE 必须带条件、优先按主键）】
        ///   第一步：按条件查出所有待删记录的主键 Id；
        ///   第二步：按主键分批 DELETE。
        ///   默认路径下，第一步的 WHERE 和第二步的语句都再带一次 PrintStatus &lt;&gt; 0 兜底 ——
        ///           即使查与删之间状态被别的环节改了，也不会把未喷的码删掉。
        ///   强制路径下不加这个兜底条件，但 DELETE 依然带主键 IN 条件，符合"DELETE 必须带条件"红线。
        ///
        /// 【事务】整个过程在一个事务里，中途失败整体回滚，不会出现删一半的情况。
        ///
        /// 【边界】可删条数为 0 时直接返回 0，不开事务、不写日志。
        /// </summary>
        /// <returns>实际删除条数</returns>
        public static int DeleteByFilter(CodeQueryFilter filter)
        {
            if (filter == null)
            {
                return 0;
            }

            bool excludeNotPrinted = !filter.AllowDeleteNotPrinted;

            List<SqliteParameter> parameters = new List<SqliteParameter>();
            string whereClause = BuildWhereClause(filter, excludeNotPrinted, parameters);

            // 未喷兜底条件：默认路径全程生效，强制清理路径下不加
            string guardSql = excludeNotPrinted ? " AND PrintStatus <> 0" : string.Empty;

            int totalDeleted = 0;

            SqliteHelper.ExecuteInTransaction(delegate (SqliteConnection connection, SqliteTransaction transaction)
            {
                // 第一步：查主键
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

                // 第二步：按主键分批删除
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

                    // 兜底条件 PrintStatus <> 0（仅默认路径）：即便状态在查询之后发生变化，未喷的码也绝不会被删
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
        // 私有辅助
        // ============================================================

        /// <summary>
        /// 拼 WHERE 子句
        /// 【无条件时】返回 "WHERE 1=1"，后面追加条件时统一用 " AND ..." 起头，调用方不用判断加不加 AND。
        /// </summary>
        /// <param name="filter">筛选条件</param>
        /// <param name="excludeNotPrinted">是否追加"排除未喷"条件（删除场景用）</param>
        /// <param name="parameters">参数承接集合</param>
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
                // CreateTime 存的是 "yyyy-MM-dd HH:mm:ss"，字典序等于时间序，可直接 BETWEEN
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

        /// <summary>按给定 WHERE 子句统计条数</summary>
        private static int GetCountByWhere(string whereClause, List<SqliteParameter> parameters)
        {
            string sql = "SELECT COUNT(1) FROM CodeData " + whereClause + ";";
            object? value = SqliteHelper.ExecuteScalar(sql, parameters.ToArray());
            return value.ToInt(0);
        }

        /// <summary>DataTable → 实体列表（手写映射，不用反射，见 Extensions 类注释）</summary>
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

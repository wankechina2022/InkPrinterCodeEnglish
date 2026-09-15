using System.Data;
using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-10] 数据查看业务层 —— 分页查询、导出、按条件批量删除
    ///
    /// 【职责边界】本层只做三件事：
    ///   1. 把界面传来的筛选条件补上默认值（页码、每页条数），交给 DAL；
    ///   2. 把 DAL 返回的实体列表转成界面能直接绑定的 DataTable（状态转中文就在这里做）；
    ///   3. 删除前算清"筛到多少、能删多少"，删除后给出准确的实删与拦截数字。
    ///
    /// 【为什么把状态转中文放在 BLL 而不是界面】
    ///   查询和导出都需要"状态 → 中文"这一步，放在界面会导致两处各写一遍，
    ///   以后改文案容易漏；集中在 EnumHelper + 本层，改一处即可。
    /// </summary>
    public static class CodeQueryBLL
    {
        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="filter">筛选条件（页码会被本方法校验并可能修正）</param>
        /// <param name="totalCount">输出：符合条件的总条数</param>
        /// <returns>可直接绑定到 DataGridView 的 DataTable</returns>
        public static DataTable Query(CodeQueryFilter filter, out int totalCount)
        {
            totalCount = 0;

            if (filter == null)
            {
                return BuildEmptyTable();
            }

            if (filter.PageSize <= 0)
            {
                filter.PageSize = ConfigHelper.PageSize;
            }
            if (filter.PageIndex < 1)
            {
                filter.PageIndex = 1;
            }

            List<CodeData> list = CodeDataDAL.QueryByFilter(filter, out totalCount);
            return ToTable(list);
        }

        /// <summary>
        /// 按当前筛选条件查询全量数据（导出用，忽略分页）
        /// </summary>
        public static DataTable QueryForExport(CodeQueryFilter filter)
        {
            if (filter == null)
            {
                return new DataTable();
            }

            return CodeDataDAL.QueryForExport(filter);
        }

        /// <summary>
        /// 取当前筛选条件下的删除预览信息（不执行删除）
        /// 【用途】删除确认框里显示的"预计删除 N 条，其中 M 条为未喷状态将跳过"来自这里。
        ///
        /// 【双路径】filter.AllowDeleteNotPrinted = true 时，可删数 = 筛选命中数（未喷不再跳过）；
        ///          false 时可删数已排除未喷，两者之差就是"未喷跳过"条数。
        /// </summary>
        /// <param name="filter">筛选条件</param>
        /// <param name="filteredCount">输出：筛选命中的总条数</param>
        /// <returns>可删除条数（默认路径下已排除未喷）</returns>
        public static int GetDeletePreview(CodeQueryFilter filter, out int filteredCount)
        {
            filteredCount = 0;

            if (filter == null)
            {
                return 0;
            }

            filteredCount = CodeDataDAL.GetCountByFilter(filter);
            int deletableCount = CodeDataDAL.GetDeleteCount(filter);

            return deletableCount;
        }

        /// <summary>
        /// 按当前筛选条件批量删除
        ///
        /// 【双路径】
        ///   filter.AllowDeleteNotPrinted = false（默认）→ 未喷一律不删；
        ///   filter.AllowDeleteNotPrinted = true          → 未喷一并删除（清理导入错误的脏数据用）。
        ///
        /// 【执行顺序】先算筛选总数 → 实际删除 → 组装结果。
        ///   默认路径下 SkippedCount（被拦截数）= 筛选总数 - 实删数，这个数字必须如实报给用户。
        /// </summary>
        public static DeleteResult DeleteByFilter(CodeQueryFilter filter)
        {
            DeleteResult result = new DeleteResult();

            if (filter == null)
            {
                result.Success = false;
                result.Message = "筛选条件无效，未执行删除";
                return result;
            }

            try
            {
                int filteredCount = CodeDataDAL.GetCountByFilter(filter);
                int deletedCount = CodeDataDAL.DeleteByFilter(filter);

                result.FilteredCount = filteredCount;
                result.DeletedCount = deletedCount;
                result.SkippedCount = filteredCount - deletedCount;
                result.AllowNotPrinted = filter.AllowDeleteNotPrinted;
                result.Success = true;
                result.Message = BuildDeleteMessage(result);

                string modeText = filter.AllowDeleteNotPrinted ? "【含未喷·强制清理】" : "【保护未喷】";
                LogHelper.Instance.Info(modeText + "批量删除完成：筛选命中 " + filteredCount.ToString()
                                        + " 条，实删 " + deletedCount.ToString()
                                        + " 条，未喷跳过 " + result.SkippedCount.ToString() + " 条");

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "删除失败：" + ex.Message;
                LogHelper.Instance.Error("批量删除失败", ex);
                return result;
            }
        }

        // ============================================================
        // 私有辅助
        // ============================================================

        /// <summary>构造界面表格结构（空表，用于无数据时也保持列头）</summary>
        private static DataTable BuildEmptyTable()
        {
            DataTable table = new DataTable();
            table.Columns.Add("序号", typeof(long));
            table.Columns.Add("码值", typeof(string));
            table.Columns.Add("状态", typeof(string));
            table.Columns.Add("导入时间", typeof(string));
            return table;
        }

        /// <summary>实体列表 → 界面表格（状态转中文）</summary>
        private static DataTable ToTable(List<CodeData> list)
        {
            DataTable table = BuildEmptyTable();

            for (int i = 0; i < list.Count; i++)
            {
                CodeData item = list[i];

                DataRow row = table.NewRow();
                row["序号"] = item.Id;
                row["码值"] = item.CodeValue;
                row["状态"] = EnumHelper.GetPrintStatusText(item.PrintStatus);
                row["导入时间"] = item.CreateTime;
                table.Rows.Add(row);
            }

            return table;
        }

        /// <summary>组装删除结果文案（区分"保护未喷"与"含未喷强制清理"两种路径）</summary>
        private static string BuildDeleteMessage(DeleteResult result)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.AppendLine("删除完成。");
            builder.AppendLine();
            builder.AppendLine("筛选命中：" + result.FilteredCount.ToString() + " 条");
            builder.AppendLine("实际删除：" + result.DeletedCount.ToString() + " 条");

            if (result.AllowNotPrinted)
            {
                builder.AppendLine("未喷跳过：0 条（本次已勾选「含未喷」，未喷数据一并删除）");
            }
            else
            {
                builder.AppendLine("未喷跳过：" + result.SkippedCount.ToString() + " 条（未喷状态的码不允许删除）");
            }

            return builder.ToString();
        }
    }
}

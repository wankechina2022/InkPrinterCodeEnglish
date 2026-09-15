using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-10] Data view business layer — paged query, export, and conditional batch delete.
    ///
    /// [Responsibility boundary] This layer does exactly three things:
    ///   1. Fill in default values for the filter conditions coming from the UI (page number, page size) and hand them to the DAL;
    ///   2. Convert the entity list returned by the DAL into a DataTable that the UI can bind to directly (status-to-text conversion happens here);
    ///   3. Before deleting, calculate precisely "how many were matched, how many can be deleted"; after deleting, report the exact
    ///      number actually deleted and the number intercepted.
    ///
    /// [Why status-to-text conversion lives in the BLL rather than the UI]
    ///   Both query and export need the "status → text" step. Putting it in the UI would mean writing it twice,
    ///   and future wording changes would easily be missed; centralizing it in EnumHelper + this layer means changing it in one place.
    /// </summary>
    public static class CodeQueryBLL
    {
        /// <summary>
        /// Paged query
        /// </summary>
        /// <param name="filter">Filter conditions (the page number is validated and possibly corrected by this method)</param>
        /// <param name="totalCount">Output: total number of matching records</param>
        /// <returns>A DataTable that can be bound directly to a DataGridView</returns>
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
        /// Query all data under the current filter conditions (for export; pagination is ignored)
        /// [2026-09-16] Superseded by the streaming ExportToFile; retained for other callers / backward compatibility.
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
        /// [2026-09-16] Streaming export (supersedes QueryForExport + ExcelHelper.ExportDataTable).
        ///
        /// [Benefit] The result set is streamed straight from the reader to the xlsx file through
        ///   CodeDataDAL.QueryForExportReader + ExcelHelper.ExportDataReader, so export no longer loads the whole
        ///   table into memory first; the visible UI behavior (file dialog, messages, columns) is unchanged.
        /// </summary>
        /// <param name="filter">Filter conditions (paging is ignored)</param>
        /// <param name="filePath">Save path (.xlsx)</param>
        /// <param name="sheetName">Worksheet name</param>
        /// <returns>Number of exported data rows. [2026-09-16] Return value added so the streaming
        ///   path can report the row count like the legacy DataTable path did.</returns>
        public static int ExportToFile(CodeQueryFilter filter, string filePath, string sheetName)
        {
            // [2026-09-16] row count is produced by the streaming writer itself
            return CodeDataDAL.QueryForExportReader(filter, delegate (SqliteDataReader reader)
            {
                return ExcelHelper.ExportDataReader(reader, filePath, sheetName);
            });
        }

        /// <summary>
        /// Get the delete preview information for the current filter conditions (does not perform the delete).
        /// [Purpose] The delete confirmation dialog's "About to delete N records, of which M are not printed and will be skipped" comes from here.
        ///
        /// [Dual path] When filter.AllowDeleteNotPrinted = true, deletable count = filter hit count (not-printed are no longer skipped);
        ///          when false, the deletable count already excludes not-printed, and the difference between the two is the "not-printed skipped" count.
        /// </summary>
        /// <param name="filter">Filter conditions</param>
        /// <param name="filteredCount">Output: total number of records matching the filter</param>
        /// <returns>Number of deletable records (the default path already excludes not-printed)</returns>
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
        /// Batch delete according to the current filter conditions
        ///
        /// [Dual path]
        ///   filter.AllowDeleteNotPrinted = false (default) → not-printed records are never deleted;
        ///   filter.AllowDeleteNotPrinted = true            → not-printed records are deleted too (used to clean up dirty data from bad imports).
        ///
        /// [Execution order] First count the filtered total → perform the actual delete → assemble the result.
        ///   On the default path, SkippedCount (the number intercepted) = filtered total - actually deleted; this number must be reported
        ///   truthfully to the user.
        /// </summary>
        public static DeleteResult DeleteByFilter(CodeQueryFilter filter)
        {
            DeleteResult result = new DeleteResult();

            if (filter == null)
            {
                result.Success = false;
                result.Message = "Invalid filter conditions; delete was not performed";
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

                string modeText = filter.AllowDeleteNotPrinted ? "[Include not printed - force clean]" : "[Protect not printed]";
                LogHelper.Instance.Info(modeText + "Batch delete complete: filter hit " + filteredCount.ToString()
                                        + " records, actually deleted " + deletedCount.ToString()
                                        + " records, not printed skipped " + result.SkippedCount.ToString() + " records");

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Delete failed: " + ex.Message;
                LogHelper.Instance.Error("Batch delete failed", ex);
                return result;
            }
        }

        // ============================================================
        // Private helpers
        // ============================================================

        /// <summary>Build the UI table structure (an empty table, so that column headers remain even when there is no data)</summary>
        private static DataTable BuildEmptyTable()
        {
            DataTable table = new DataTable();
            table.Columns.Add("No.", typeof(long));
            table.Columns.Add("Code Value", typeof(string));
            table.Columns.Add("Status", typeof(string));
            table.Columns.Add("Import Time", typeof(string));
            return table;
        }

        /// <summary>Entity list → UI table (status converted to text)</summary>
        private static DataTable ToTable(List<CodeData> list)
        {
            DataTable table = BuildEmptyTable();

            for (int i = 0; i < list.Count; i++)
            {
                CodeData item = list[i];

                DataRow row = table.NewRow();
                row["No."] = item.Id;
                row["Code Value"] = item.CodeValue;
                row["Status"] = EnumHelper.GetPrintStatusText(item.PrintStatus);
                row["Import Time"] = item.CreateTime;
                table.Rows.Add(row);
            }

            return table;
        }

        /// <summary>Assemble the delete result message (distinguishing the "protect not printed" and "include not printed, forced cleanup" paths)</summary>
        private static string BuildDeleteMessage(DeleteResult result)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.AppendLine("Delete completed.");
            builder.AppendLine();
            builder.AppendLine("Filter matched: " + result.FilteredCount.ToString() + " records");
            builder.AppendLine("Actually deleted: " + result.DeletedCount.ToString() + " records");

            if (result.AllowNotPrinted)
            {
                builder.AppendLine("Not printed skipped: 0 records (\"Include not printed\" was checked this time, so not-printed data was deleted as well)");
            }
            else
            {
                builder.AppendLine("Not printed skipped: " + result.SkippedCount.ToString() + " records (codes in the not-printed state may not be deleted)");
            }

            return builder.ToString();
        }
    }
}

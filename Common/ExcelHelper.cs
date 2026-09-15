using System.Data;
using System.Globalization;
using NPOI.SS.UserModel;
using NPOI.XSSF.Streaming;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Excel 读写帮助类（基于 NPOI）—— 导入取第一列、导出查询结果
    ///
    /// 【导入格式（万总确认）】只取第一个 Sheet 的第一列，无表头，其他列一律忽略。
    ///
    /// 【读取机制】
    ///   - WorkbookFactory.Create 自动识别 .xls（HSSF）和 .xlsx（XSSF），调用方不用分流。
    ///   - 逐行取第一列，空行用空串占位，保证"结果集下标 + 1 = Excel 行号"，排查问题能对上原始位置。
    ///   - 每 2000 行检查一次取消信号。
    ///
    /// 【关键坑：数字型单元格的科学计数法】
    ///   Excel 数字单元格底层是 double。若直接 cell.ToString() 或把 double 直接 ToString()，
    ///   18 位长码会变成 "1.23457E+17" 入库，数据直接报废。
    ///   本类的 FormatNumericCell 对整数一律用 long 输出，规避科学计数法。
    ///   ⚠ 但这只能救"格式问题"，救不了"精度问题"：double 只能精确表示 15~16 位整数，
    ///   如果码超过 15 位且在 Excel 里存成了「数值」格式，Excel 自身就已经丢掉尾数了。
    ///   因此现场约定：Excel 里的码值列必须设为「文本」格式（CellType.String），才能保证长码无损。
    ///
    /// 【导出机制】用 SXSSFWorkbook 流式写（内存只保留 1000 行滑动窗口），10 万行导出不会撑爆内存；
    ///   所有单元格一律按字符串写入，避免导出的码在 Excel 里又被识别成数字变科学计数法。
    ///
    /// 【资源释放】NPOI 的 workbook 和文件流都是非托管相关资源，全部用 try/finally 显式释放，
    ///   SXSSFWorkbook 还必须 Dispose 清理它自己产生的临时文件，否则临时目录会越堆越大。
    /// </summary>
    public static class ExcelHelper
    {
        /// <summary>取消检查的行间隔</summary>
        private const int CANCEL_CHECK_STEP = 2000;

        // 说明：SXSSFWorkbook 用无参构造 —— 采用 NPOI 默认的内存滑动窗口（100 行），
        // 超出窗口的行自动落到临时文件，10 万行导出内存依然平稳。
        // 这里刻意不传自定义窗口大小，避免依赖特定重载签名，减少版本升级时的编译风险。

        // ============================================================
        // 读取（导入用）
        // ============================================================

        /// <summary>
        /// 读取 Excel 第一个 Sheet 的第一列
        /// </summary>
        /// <param name="filePath">Excel 文件完整路径（.xls / .xlsx）</param>
        /// <param name="values">承接结果的集合（由调用方创建，方法内只往里 Add）</param>
        /// <param name="token">取消信号</param>
        /// <returns>读到的总行数（等于 values 新增的条数）</returns>
        public static int ReadFirstColumn(string filePath, List<string> values, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Excel 文件路径不能为空", nameof(filePath));
            }
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values), "承接结果的集合不能为 null");
            }
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("待导入的 Excel 文件不存在：" + filePath, filePath);
            }

            int totalRows = 0;
            FileStream? stream = null;
            IWorkbook? workbook = null;

            try
            {
                stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                workbook = WorkbookFactory.Create(stream);

                if (workbook.NumberOfSheets <= 0)
                {
                    LogHelper.Instance.Warn("Excel 文件不含任何工作表：" + filePath);
                    return 0;
                }

                ISheet sheet = workbook.GetSheetAt(0);
                if (sheet == null)
                {
                    LogHelper.Instance.Warn("Excel 第一个工作表读取为空：" + filePath);
                    return 0;
                }

                // LastRowNum 是 0 基的最后一行下标，因此总行数 = LastRowNum + 1
                int lastRowIndex = sheet.LastRowNum;

                for (int rowIndex = 0; rowIndex <= lastRowIndex; rowIndex++)
                {
                    totalRows++;

                    IRow row = sheet.GetRow(rowIndex);
                    if (row == null)
                    {
                        // 整行为空：占位空串，保持行号对应关系
                        values.Add(string.Empty);
                    }
                    else
                    {
                        ICell cell = row.GetCell(0);
                        values.Add(GetCellText(cell));
                    }

                    if (totalRows % CANCEL_CHECK_STEP == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            finally
            {
                if (workbook != null)
                {
                    try
                    {
                        workbook.Close();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("关闭 Excel 工作簿失败：" + ex.Message);
                    }

                    IDisposable? disposableWorkbook = workbook as IDisposable;
                    if (disposableWorkbook != null)
                    {
                        try
                        {
                            disposableWorkbook.Dispose();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("释放 Excel 工作簿失败：" + ex.Message);
                        }
                    }
                }

                if (stream != null)
                {
                    stream.Dispose();
                }
            }

            return totalRows;
        }

        // ============================================================
        // 导出（数据查看页用）
        // ============================================================

        /// <summary>
        /// 把 DataTable 导出为 xlsx 文件
        ///
        /// 【边界】
        ///   - 目标目录不存在时自动创建（开发规约要求）；
        ///   - table 为 null 或无列时直接抛异常，避免生成一个空壳文件误导用户；
        ///   - 目标文件已存在会被覆盖，是否覆盖由调用方（SaveFileDialog）负责向用户确认。
        /// </summary>
        /// <param name="table">待导出的数据，列名即表头</param>
        /// <param name="filePath">保存路径（.xlsx）</param>
        /// <param name="sheetName">工作表名，为空时用 Sheet1</param>
        public static void ExportDataTable(DataTable table, string filePath, string sheetName)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table), "待导出的数据不能为 null");
            }
            if (table.Columns.Count <= 0)
            {
                throw new ArgumentException("待导出的数据没有任何列", nameof(table));
            }
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("导出路径不能为空", nameof(filePath));
            }

            string? folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                if (!ValidateHelper.EnsureFolderExists(folder))
                {
                    throw new IOException("导出目录不可用：" + folder);
                }
            }

            string finalSheetName = "Sheet1";
            if (!string.IsNullOrWhiteSpace(sheetName))
            {
                finalSheetName = sheetName;
            }

            SXSSFWorkbook? workbook = null;
            FileStream? stream = null;

            try
            {
                workbook = new SXSSFWorkbook();
                ISheet sheet = workbook.CreateSheet(finalSheetName);

                // 表头
                IRow headerRow = sheet.CreateRow(0);
                for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                {
                    ICell headerCell = headerRow.CreateCell(columnIndex);
                    headerCell.SetCellValue(table.Columns[columnIndex].ColumnName);
                }

                // 数据行：全部按字符串写入，防止长码被 Excel 识别成数字后显示成科学计数法
                for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    IRow dataRow = sheet.CreateRow(rowIndex + 1);
                    for (int columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                    {
                        ICell dataCell = dataRow.CreateCell(columnIndex);
                        dataCell.SetCellValue(table.Rows[rowIndex][columnIndex].ToSafeString());
                    }
                }

                stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                // 第二个参数 leaveOpen=false：写完让 NPOI 自己收尾，文件流由本方法 finally 里再显式释放一次（幂等）
                workbook.Write(stream, false);
            }
            finally
            {
                if (workbook != null)
                {
                    try
                    {
                        workbook.Close();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("关闭导出工作簿失败：" + ex.Message);
                    }

                    // SXSSFWorkbook 会把超出内存窗口的行写到临时文件，必须 Dispose 才能清理掉
                    IDisposable? disposableWorkbook = workbook as IDisposable;
                    if (disposableWorkbook != null)
                    {
                        try
                        {
                            disposableWorkbook.Dispose();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("清理导出工作簿临时文件失败：" + ex.Message);
                        }
                    }
                }

                if (stream != null)
                {
                    stream.Dispose();
                }
            }
        }

        // ============================================================
        // 私有：单元格取值
        // ============================================================

        /// <summary>
        /// 取单元格文本
        /// 【双路径】普通单元格按其 CellType 取值；公式单元格取「缓存结果」而不重算 —— 
        ///   10 万行重算公式极慢，且导入场景只关心当前显示值。
        /// </summary>
        private static string GetCellText(ICell? cell)
        {
            if (cell == null)
            {
                return string.Empty;
            }

            switch (cell.CellType)
            {
                case CellType.String:
                    if (cell.StringCellValue == null)
                    {
                        return string.Empty;
                    }
                    return cell.StringCellValue;

                case CellType.Numeric:
                    return FormatNumericCell(cell);

                case CellType.Boolean:
                    if (cell.BooleanCellValue)
                    {
                        return "TRUE";
                    }
                    return "FALSE";

                case CellType.Formula:
                    return GetFormulaCellText(cell);

                case CellType.Blank:
                    return string.Empty;

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// 数字单元格转文本 —— 规避科学计数法
        /// 【机制】整数（小数部分为 0）用 long 输出；确有小数的按最多 10 位小数输出，末尾不补 0。
        /// 【边界】NaN / 无穷大返回空串（这类值会被上层判为空行跳过）。
        /// </summary>
        private static string FormatNumericCell(ICell cell)
        {
            double numericValue = 0d;
            try
            {
                numericValue = cell.NumericCellValue;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("读取数字单元格失败：" + ex.Message);
                return string.Empty;
            }

            if (double.IsNaN(numericValue) || double.IsInfinity(numericValue))
            {
                return string.Empty;
            }

            bool isInteger = Math.Abs(numericValue - Math.Floor(numericValue)) < 0.0000001;
            bool inLongRange = Math.Abs(numericValue) < 9.0E18;

            if (isInteger && inLongRange)
            {
                long longValue = (long)numericValue;
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            return numericValue.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 公式单元格取缓存结果
        /// 【边界】取值过程中任何异常都返回空串，不能因为一个公式单元格把整个导入中断。
        /// </summary>
        private static string GetFormulaCellText(ICell cell)
        {
            try
            {
                switch (cell.CachedFormulaResultType)
                {
                    case CellType.String:
                        if (cell.StringCellValue == null)
                        {
                            return string.Empty;
                        }
                        return cell.StringCellValue;

                    case CellType.Numeric:
                        return FormatNumericCell(cell);

                    case CellType.Boolean:
                        if (cell.BooleanCellValue)
                        {
                            return "TRUE";
                        }
                        return "FALSE";

                    default:
                        return string.Empty;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("读取公式单元格失败：" + ex.Message);
                return string.Empty;
            }
        }
    }
}

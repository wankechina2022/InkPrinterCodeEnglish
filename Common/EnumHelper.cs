using InkPrinterCode.Model;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 枚举转换帮助类 —— 枚举与界面文字、数据库 int 值、文件扩展名之间的转换全部集中在这里
    ///
    /// 【为什么集中】开发规约要求"多使用枚举，转换类写成公共方法"。
    ///   状态中文名如果散落在各窗体里写 switch，改文案就得满项目找；集中一处，改一次全局生效。
    ///
    /// 【写法说明】统一用传统 switch 语句 + 明确的 default 分支，不用 switch 表达式，便于逐行调试。
    ///   default 分支不抛异常，而是返回"未知(值)"字样 —— 万一库里出现意料外的数值，
    ///   界面照样能显示出来（显示成"未知(9)"），不会因为一条脏数据把整个列表刷不出来。
    /// </summary>
    public static class EnumHelper
    {
        /// <summary>约定：下拉框里"全部"选项的值</summary>
        public const int ALL_OPTION_VALUE = -1;

        // ============================================================
        // 喷印状态
        // ============================================================

        /// <summary>
        /// 取喷印状态的中文显示文字
        /// </summary>
        public static string GetPrintStatusText(PrintStatus status)
        {
            switch (status)
            {
                case PrintStatus.NotPrinted:
                    return "未喷";
                case PrintStatus.Printed:
                    return "已喷";
                case PrintStatus.Failed:
                    return "失败";
                case PrintStatus.Voided:
                    return "作废";
                default:
                    return "未知(" + ((int)status).ToString() + ")";
            }
        }

        /// <summary>
        /// 取喷印状态的中文显示文字（int 重载）
        /// 【用途】DataGridView 绑定的是从库里读出的 int 值，直接用这个重载转文字，省去外部强转。
        /// </summary>
        public static string GetPrintStatusText(int statusValue)
        {
            return GetPrintStatusText(ParsePrintStatus(statusValue));
        }

        /// <summary>
        /// 把数据库中的 int 值转成枚举
        /// 【边界】遇到未定义的数值不抛异常，原样强转返回（配合 GetPrintStatusText 显示成"未知(值)"），
        ///   由界面把脏数据暴露出来，而不是让程序崩掉。
        /// </summary>
        public static PrintStatus ParsePrintStatus(int statusValue)
        {
            switch (statusValue)
            {
                case 0:
                    return PrintStatus.NotPrinted;
                case 1:
                    return PrintStatus.Printed;
                case 2:
                    return PrintStatus.Failed;
                case 3:
                    return PrintStatus.Voided;
                default:
                    return (PrintStatus)statusValue;
            }
        }

        /// <summary>
        /// 生成喷印状态下拉框数据源
        /// </summary>
        /// <param name="includeAllOption">是否在最前面加一个"全部"选项（值为 -1）</param>
        public static List<ComboItem> GetPrintStatusItems(bool includeAllOption)
        {
            List<ComboItem> items = new List<ComboItem>();

            if (includeAllOption)
            {
                items.Add(new ComboItem(ALL_OPTION_VALUE, "全部"));
            }

            items.Add(new ComboItem((int)PrintStatus.NotPrinted, GetPrintStatusText(PrintStatus.NotPrinted)));
            items.Add(new ComboItem((int)PrintStatus.Printed, GetPrintStatusText(PrintStatus.Printed)));
            items.Add(new ComboItem((int)PrintStatus.Failed, GetPrintStatusText(PrintStatus.Failed)));
            items.Add(new ComboItem((int)PrintStatus.Voided, GetPrintStatusText(PrintStatus.Voided)));

            return items;
        }

        // ============================================================
        // 导入来源类型
        // ============================================================

        /// <summary>
        /// 取导入来源类型的中文显示文字
        /// </summary>
        public static string GetSourceTypeText(ImportSourceType sourceType)
        {
            switch (sourceType)
            {
                case ImportSourceType.Txt:
                    return "文本文件";
                case ImportSourceType.Excel:
                    return "Excel文件";
                default:
                    return "未知(" + ((int)sourceType).ToString() + ")";
            }
        }

        /// <summary>
        /// 根据文件扩展名判断导入来源类型
        ///
        /// 【双路径说明】
        ///   支持的扩展名：.txt → Txt；.xls / .xlsx → Excel，supported 返回 true；
        ///   其他扩展名（含无扩展名、路径为空）：supported 返回 false，此时返回值无意义，
        ///   调用方必须直接拒绝导入 —— 不允许"猜"格式去硬读，避免把二进制文件当文本读出满屏乱码入库。
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="supported">是否为受支持的格式</param>
        /// <returns>受支持时返回对应的来源类型；不受支持时返回 Txt（无意义，调用方须先判 supported）</returns>
        public static ImportSourceType GetSourceTypeByExtension(string? filePath, out bool supported)
        {
            supported = false;

            if (string.IsNullOrWhiteSpace(filePath))
            {
                return ImportSourceType.Txt;
            }

            string extension = string.Empty;
            try
            {
                extension = Path.GetExtension(filePath).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("解析文件扩展名失败：" + filePath + "，" + ex.Message);
                return ImportSourceType.Txt;
            }

            if (extension == ".txt")
            {
                supported = true;
                return ImportSourceType.Txt;
            }

            if (extension == ".xls" || extension == ".xlsx")
            {
                supported = true;
                return ImportSourceType.Excel;
            }

            return ImportSourceType.Txt;
        }
    }
}

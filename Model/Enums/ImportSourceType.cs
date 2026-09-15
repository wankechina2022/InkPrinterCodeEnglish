namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 数据导入来源类型枚举
    ///
    /// 【取值约定】数值与数据库 ImportBatch.SourceType 列一一对应：
    ///   0 = Txt   ：纯文本文件，一行一码，分隔符为回车换行
    ///   1 = Excel ：Excel 文件（.xls / .xlsx），只取第一个 Sheet 的第一列、无表头
    ///
    /// 【边界】不支持的扩展名不落此枚举，由 EnumHelper.GetSourceTypeByExtension 返回 supported=false，
    ///         调用方须直接拒绝导入，不允许猜测格式。
    /// </summary>
    public enum ImportSourceType
    {
        /// <summary>纯文本文件（0）—— 一行一码</summary>
        Txt = 0,

        /// <summary>Excel 文件（1）—— 第一个 Sheet 第一列，无表头</summary>
        Excel = 1
    }
}

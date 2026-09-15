namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 导入批次台账实体 —— 对应 SQLite 表 ImportBatch
    ///
    /// 【用途】纯后台追溯用：记录每次导入的文件来源与四项计数，出问题时能查清"这批码是哪个文件、
    ///   什么时候、谁导进来的、有效多少条"。数据查看页按万总要求不提供批次筛选，界面上不暴露批次概念。
    ///
    /// 【写入时机】导入开始时先插入一条（拿到自增 Id 供 CodeData.BatchId 关联），
    ///   导入结束后回写四项计数。即使本次 0 条有效（全是重复码），台账也保留记录，方便追溯。
    ///
    /// 【计数口径】TotalRows = ValidCount + DuplicateCount + InvalidCount
    ///   ValidCount     ：实际入库条数
    ///   DuplicateCount ：文件内重复 + 库内已存在，合计
    ///   InvalidCount   ：空行 + 含中文字符的行，合计
    /// </summary>
    public class ImportBatch
    {
        /// <summary>主键，自增</summary>
        public long Id { get; set; } = 0;

        /// <summary>批次号，格式 yyyyMMddHHmmssfff，库中建有唯一索引</summary>
        public string BatchNo { get; set; } = string.Empty;

        /// <summary>来源类型：Txt / Excel</summary>
        public ImportSourceType SourceType { get; set; } = ImportSourceType.Txt;

        /// <summary>源文件名（不含路径）</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>源文件完整路径</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>源文件总行数（含空行、重复行）</summary>
        public int TotalRows { get; set; } = 0;

        /// <summary>有效入库条数</summary>
        public int ValidCount { get; set; } = 0;

        /// <summary>重复条数（文件内重复 + 库内已存在）</summary>
        public int DuplicateCount { get; set; } = 0;

        /// <summary>无效条数（空行 + 含中文）</summary>
        public int InvalidCount { get; set; } = 0;

        /// <summary>导入时间，格式 yyyy-MM-dd HH:mm:ss</summary>
        public string ImportTime { get; set; } = string.Empty;

        /// <summary>操作人，默认取当前 Windows 登录用户名</summary>
        public string Operator { get; set; } = string.Empty;

        /// <summary>备注，例如"用户取消导入"等说明</summary>
        public string Remark { get; set; } = string.Empty;
    }
}

namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 一次导入的执行结果 —— BLL 返回给 UI 层用于弹框展示与日志汇总
    ///
    /// 【为什么把计数拆这么细】导入完要弹框告诉万总"总共多少行、入库多少、重复多少、无效多少"，
    ///   出现异常数据时还得能进一步说清"重复是文件内重复还是库里已经有了""无效是空行还是含中文"，
    ///   所以分项计数都单独留字段，UI 想显示哪几项由 UI 决定。
    ///
    /// 【计数恒等式】TotalRows = ValidCount + DuplicateCount + InvalidCount
    ///   DuplicateCount = FileDuplicateCount + DbDuplicateCount
    ///   InvalidCount   = EmptyRowCount + ChineseRowCount
    ///
    /// 【三种收尾状态】
    ///   Success=true, Canceled=false  → 正常完成（即使 ValidCount=0 也算完成）
    ///   Success=false, Canceled=true  → 用户点了取消，事务已回滚，一条都没入库
    ///   Success=false, Canceled=false → 出错失败，Message 里带友好错误提示，事务已回滚
    /// </summary>
    public class ImportResult
    {
        /// <summary>是否成功完成</summary>
        public bool Success { get; set; } = false;

        /// <summary>是否被用户取消（取消后事务回滚，不会有任何数据入库）</summary>
        public bool Canceled { get; set; } = false;

        /// <summary>结果说明 / 失败原因（面向用户的友好文案，不含堆栈）</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>本次生成的批次 Id，0 表示批次台账都没写成</summary>
        public long BatchId { get; set; } = 0;

        /// <summary>本次生成的批次号</summary>
        public string BatchNo { get; set; } = string.Empty;

        /// <summary>源文件总行数</summary>
        public int TotalRows { get; set; } = 0;

        /// <summary>实际入库条数</summary>
        public int ValidCount { get; set; } = 0;

        /// <summary>重复总数 = 文件内重复 + 库内已存在</summary>
        public int DuplicateCount { get; set; } = 0;

        /// <summary>文件内自身重复的条数（同一文件里出现多次，只保留第一次）</summary>
        public int FileDuplicateCount { get; set; } = 0;

        /// <summary>库内已存在的条数（跨批次、跨文件都算重复）</summary>
        public int DbDuplicateCount { get; set; } = 0;

        /// <summary>无效总数 = 空行 + 含中文</summary>
        public int InvalidCount { get; set; } = 0;

        /// <summary>空行条数（Trim 后为空）</summary>
        public int EmptyRowCount { get; set; } = 0;

        /// <summary>含中文字符被忽略的条数</summary>
        public int ChineseRowCount { get; set; } = 0;

        /// <summary>本次导入耗时（毫秒）</summary>
        public long ElapsedMs { get; set; } = 0;
    }
}

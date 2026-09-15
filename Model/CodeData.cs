namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 码数据实体 —— 对应 SQLite 表 CodeData，一条记录 = 一个待喷/已喷的码
    ///
    /// 【字段与库表一一对应】属性名与列名保持完全一致，方便 DAL 手写映射时对照检查。
    ///
    /// 【时间为什么用 string】SQLite 没有原生 datetime 类型，本项目统一以 TEXT 存
    ///   "yyyy-MM-dd HH:mm:ss" 格式字符串 —— 该格式的字典序等于时间序，可直接用 BETWEEN 做区间筛选，
    ///   避免 DateTime 与 TEXT 之间来回转换产生的格式歧义和 DateTime.MinValue 脏值。
    ///   需要 DateTime 时用 Extensions.ToDateTimeOrDefault 转换，写库时用 Extensions.ToDbTimeString 生成。
    ///
    /// 【默认值】所有属性均给默认值（开发规约「所有定义的变量必须有默认值」），
    ///   字符串默认 string.Empty 而非 null，避免调用方到处判空。
    /// </summary>
    public class CodeData
    {
        /// <summary>主键，自增。删除操作一律按此主键进行</summary>
        public long Id { get; set; } = 0;

        /// <summary>所属导入批次 Id（对应 ImportBatch.Id），0 表示未关联批次</summary>
        public long BatchId { get; set; } = 0;

        /// <summary>在源文件中的行号（从 1 开始），排查导入问题时用于回溯原始文件位置</summary>
        public int RowNo { get; set; } = 0;

        /// <summary>码值。全局唯一（库中建有 UNIQUE 索引），导入时仅做 Trim，不做长度与字符集限制</summary>
        public string CodeValue { get; set; } = string.Empty;

        /// <summary>喷印状态，默认未喷</summary>
        public PrintStatus PrintStatus { get; set; } = PrintStatus.NotPrinted;

        /// <summary>发送给喷码机的时间，格式 yyyy-MM-dd HH:mm:ss，未发送时为空串（阶段二使用）</summary>
        public string SendTime { get; set; } = string.Empty;

        /// <summary>喷印完成时间（收到喷码机成功返回值的时刻），未完成时为空串（阶段二使用）</summary>
        public string PrintTime { get; set; } = string.Empty;

        /// <summary>喷码机返回内容原文，失败时保留原始报文便于排障（阶段二使用）</summary>
        public string FeedbackText { get; set; } = string.Empty;

        /// <summary>重发次数，失败重试时累加（阶段二使用）</summary>
        public int RetryCount { get; set; } = 0;

        /// <summary>入库时间（即导入时间），格式 yyyy-MM-dd HH:mm:ss。数据查看页的时间范围筛选基于此列</summary>
        public string CreateTime { get; set; } = string.Empty;
    }
}

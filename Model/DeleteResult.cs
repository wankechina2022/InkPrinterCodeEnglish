namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 批量删除的执行结果 —— BLL 返回给界面做结果提示
    ///
    /// 【为什么要区分三个数字】删除是按"当前筛选条件"批量执行的，用户看不到具体删了哪几条，
    ///   必须在结果里说清楚三件事，否则误删了没人知道：
    ///   FilteredCount  当前筛选一共命中多少条（用户看到的范围）
    ///   DeletedCount   实际删掉多少条
    ///   SkippedCount   因为"未喷状态不允许删除"被拦下多少条
    ///
    /// 【恒等式】FilteredCount = DeletedCount + SkippedCount
    ///   （失败时 DeletedCount=0、SkippedCount=FilteredCount，并在 Message 里说明原因）
    ///
    /// 【业务约束（2026-09-10 调整）】默认路径下未喷（PrintStatus=0）的码不允许删除 ——
    ///   DAL 的 SQL 里硬编码了 PrintStatus &lt;&gt; 0 兜底，界面层拦不住时数据库层也拦得住。
    ///   但导入进来的码全部是未喷状态，纯硬规则会形成"导错数据删不掉"的死锁，
    ///   因此另开一条强制清理通道（AllowNotPrinted=true），由用户显式勾选并二次确认后才生效。
    /// </summary>
    public class DeleteResult
    {
        /// <summary>是否执行成功</summary>
        public bool Success { get; set; } = false;

        /// <summary>结果说明 / 失败原因（面向用户的友好文案）</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>当前筛选命中的总条数</summary>
        public int FilteredCount { get; set; } = 0;

        /// <summary>实际删除条数</summary>
        public int DeletedCount { get; set; } = 0;

        /// <summary>因未喷状态被拦截、未删除的条数</summary>
        public int SkippedCount { get; set; } = 0;

        /// <summary>
        /// [2026-09-10] 本次删除是否走的是"含未喷"强制清理通道
        /// true = 未喷数据也一并删除（勾选"含未喷"后才会出现），此时 SkippedCount 恒为 0。
        /// </summary>
        public bool AllowNotPrinted { get; set; } = false;
    }
}

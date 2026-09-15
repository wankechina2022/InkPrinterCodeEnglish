namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 数据查看页的查询条件对象
    ///
    /// 【筛选项范围】按万总确认：只保留「喷印状态」+「时间范围」两个条件，
    ///   不做批次筛选、不做码值模糊查询。
    ///
    /// 【为什么用 bool 开关而不是可空类型】用 HasStatusFilter / HasTimeFilter 两个显式开关，
    ///   比 PrintStatus? 这类可空写法更直白 —— DAL 拼 WHERE 时一眼能看出某个条件到底加不加，
    ///   也避免"值为 0（未喷）"被误当成"没有传值"处理。
    ///
    /// 【时间格式】StartTime / EndTime 必须是 "yyyy-MM-dd HH:mm:ss" 格式字符串，
    ///   与库中 CreateTime 列的存储格式完全一致，直接参与 BETWEEN 比较。
    ///   由 UI 层的 DateTimePicker 通过 Extensions.ToDbTimeString 生成，
    ///   习惯上开始时间取当天 00:00:00、结束时间取当天 23:59:59。
    ///
    /// 【分页边界】PageIndex 从 1 开始；PageSize 由配置项 PageSize 提供（默认 100）。
    ///   导出功能复用同一个筛选条件对象，但忽略分页字段（导出当前筛选的全量结果）。
    /// </summary>
    public class CodeQueryFilter
    {
        /// <summary>是否启用状态筛选。false = 查全部状态</summary>
        public bool HasStatusFilter { get; set; } = false;

        /// <summary>状态筛选值，仅当 HasStatusFilter = true 时生效</summary>
        public PrintStatus Status { get; set; } = PrintStatus.NotPrinted;

        /// <summary>是否启用时间范围筛选。false = 不限时间</summary>
        public bool HasTimeFilter { get; set; } = false;

        /// <summary>开始时间（含），格式 yyyy-MM-dd HH:mm:ss</summary>
        public string StartTime { get; set; } = string.Empty;

        /// <summary>结束时间（含），格式 yyyy-MM-dd HH:mm:ss</summary>
        public string EndTime { get; set; } = string.Empty;

        /// <summary>当前页码，从 1 开始</summary>
        public int PageIndex { get; set; } = 1;

        /// <summary>每页条数</summary>
        public int PageSize { get; set; } = 100;

        /// <summary>
        /// [2026-09-10] 删除时是否允许连「未喷」状态的数据一起删（默认 false = 不允许）
        ///
        /// 【为什么要有这个开关】
        ///   导入进来的码全部是「未喷」状态。原先"未喷一律不删"是条硬规则，
        ///   但它带来一个死锁：万一导入了错数据（典型场景是 Excel 码值列没设成文本格式，
        ///   超 15 位长码被 Excel 自身丢掉尾数），这批脏数据既删不掉、又占着唯一索引，
        ///   正确的码再导一遍还会被判重复 —— 整批码被彻底堵死。
        ///   所以必须留一条"确认后强制清理"的通道。
        ///
        /// 【安全设计】默认关闭。界面上要显式勾选"含未喷"才会置 true，
        ///   且勾选后必须再过一次强确认框（见 DataViewForm.btnDelete_Click）。
        ///   不勾时行为与以前完全一致：未喷数据一条都不会被删。
        ///
        /// 【DAL 侧对应行为】
        ///   true  → 统计与删除都不再追加 PrintStatus &lt;&gt; 0，未喷数据一并删除；
        ///   false → 统计与删除都追加 PrintStatus &lt;&gt; 0，未喷数据全程被排除。
        /// </summary>
        public bool AllowDeleteNotPrinted { get; set; } = false;
    }
}

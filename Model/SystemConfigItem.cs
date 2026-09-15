namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 系统参数配置项实体（对应 SystemConfig 表，键值对）
    ///
    /// 【用途】系统参数配置窗体的显示与保存载体 —— 页面上每个可调参数
    ///   （心跳周期、超时、重试次数、分页条数等）都对应一条记录。
    ///
    /// 【字段默认值】所有字段全部给默认值（开发规约），不允许 null。
    /// </summary>
    public class SystemConfigItem
    {
        /// <summary>参数键名（主键，如 HeartbeatIntervalMs）</summary>
        public string ConfigKey { get; set; } = string.Empty;

        /// <summary>参数值（统一字符串存储，读取方按类型转换）</summary>
        public string ConfigValue { get; set; } = string.Empty;

        /// <summary>中文说明（给后台查库排查用，页面不显示）</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>最后更新时间（TEXT，yyyy-MM-dd HH:mm:ss）</summary>
        public string UpdateTime { get; set; } = string.Empty;
    }
}

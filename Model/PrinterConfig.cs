using InkPrinterCode.Model.Enums;

namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] 喷码机连接配置实体（对应 PrinterConfig 表）
    ///
    /// 【双行设计】表中固定两行：ConnType=0（TCP）一行、ConnType=1（串口）一行，
    ///   各存各的参数；IsEnabled=1 的那一行是"当前启用"的连接方式。
    ///   切换只改两行的 IsEnabled（同一事务内一行置 1、另一行置 0），
    ///   两行的参数互不覆盖 —— 来回切换配置内容不丢（万总要求）。
    ///
    /// 【字段默认值】所有字段全部给默认值（开发规约：所有变量给默认值），不允许出现 null。
    /// </summary>
    public class PrinterConfig
    {
        /// <summary>主键（每行固定：TCP 行 / 串口行）</summary>
        public int Id { get; set; } = 0;

        /// <summary>连接类型：0=TCP 1=Serial</summary>
        public ConnType ConnType { get; set; } = ConnType.Tcp;

        /// <summary>启用标志：1=当前启用（两行中最多一行启用）</summary>
        public int IsEnabled { get; set; } = 0;

        // ---------- TCP 专用参数 ----------

        /// <summary>TCP 连接 IP 地址（如 192.168.1.100）</summary>
        public string TcpIp { get; set; } = string.Empty;

        /// <summary>TCP 端口，CodeNet 协议固定 7000</summary>
        public int TcpPort { get; set; } = 7000;

        /// <summary>
        /// 连接前预复位端口（现场经验：A200+ 连接前先向 700 端口发复位指令更稳定）
        /// 0 = 禁用预复位
        /// </summary>
        public int ResetPort { get; set; } = 700;

        // ---------- 串口专用参数 ----------

        /// <summary>串口号（如 COM3）</summary>
        public string SerialPortName { get; set; } = string.Empty;

        /// <summary>波特率（9600/19200/38400/57600/115200）</summary>
        public int SerialBaudRate { get; set; } = 9600;

        /// <summary>最后更新时间（TEXT，yyyy-MM-dd HH:mm:ss）</summary>
        public string UpdateTime { get; set; } = string.Empty;

        /// <summary>当前行是否为启用行（IsEnabled == 1 的便捷判断）</summary>
        public bool IsEnabledRow
        {
            get { return IsEnabled == 1; }
        }
    }
}

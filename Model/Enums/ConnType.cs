namespace InkPrinterCode.Model.Enums
{
    /// <summary>
    /// [2026-09-10] 喷码机连接类型
    ///
    /// 【说明】与 PrinterConfig 表的 ConnType 字段一一对应（库中存整数）。
    ///   TCP  = 网口连接（CodeNet 协议固定 7000 端口，A200+ 及以上机型支持）；
    ///   Serial = RS232 串口连接（A200/A300 只支持串口）。
    ///   两种连接的配置在 PrinterConfig 表中各占一行，IsEnabled 标志决定当前启用哪一种，
    ///   切换时只改标志、参数互不覆盖（万总要求：来回切换配置内容不丢）。
    /// </summary>
    public enum ConnType
    {
        /// <summary>TCP 网口连接（默认 0）</summary>
        Tcp = 0,

        /// <summary>RS232 串口连接（1）</summary>
        Serial = 1
    }
}

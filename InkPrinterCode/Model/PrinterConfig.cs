using InkPrinterCode.Model.Enums;

namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] Inkjet printer connection configuration entity (maps to the PrinterConfig table)
    ///
    /// [Two-row design] The table always has two rows: one with ConnType=0 (TCP) and one with ConnType=1 (serial),
    ///   each storing its own parameters; the row with IsEnabled=1 is the "currently enabled" connection type.
    ///   Switching only changes IsEnabled on the two rows (one set to 1 and the other to 0 within the same
    ///   transaction); the parameters of the two rows never overwrite each other -- switching back and forth loses
    ///   no configuration (requirement).
    ///
    /// [Field defaults] Every field has a default (development convention: all variables get defaults); null is not allowed.
    /// </summary>
    public class PrinterConfig
    {
        /// <summary>Primary key (fixed per row: the TCP row / the serial row)</summary>
        public int Id { get; set; } = 0;

        /// <summary>Connection type: 0=TCP 1=Serial</summary>
        public ConnType ConnType { get; set; } = ConnType.Tcp;

        /// <summary>Enabled flag: 1 = currently enabled (at most one of the two rows is enabled)</summary>
        public int IsEnabled { get; set; } = 0;

        // ---------- TCP-specific parameters ----------

        /// <summary>TCP connection IP address (e.g. 192.168.1.100)</summary>
        public string TcpIp { get; set; } = string.Empty;

        /// <summary>TCP port; the CodeNet protocol uses a fixed 7000</summary>
        public int TcpPort { get; set; } = 7000;

        /// <summary>
        /// Pre-reset port before connecting (field experience: on A200+ it is more stable to send a reset command
        /// to port 700 before connecting)
        /// 0 = disable pre-reset
        /// </summary>
        public int ResetPort { get; set; } = 700;

        // ---------- Serial-specific parameters ----------

        /// <summary>Serial port name (e.g. COM3)</summary>
        public string SerialPortName { get; set; } = string.Empty;

        /// <summary>Baud rate (9600/19200/38400/57600/115200)</summary>
        public int SerialBaudRate { get; set; } = 9600;

        /// <summary>Last update time (TEXT, yyyy-MM-dd HH:mm:ss)</summary>
        public string UpdateTime { get; set; } = string.Empty;

        /// <summary>Whether this row is the enabled row (convenience check for IsEnabled == 1)</summary>
        public bool IsEnabledRow
        {
            get { return IsEnabled == 1; }
        }
    }
}

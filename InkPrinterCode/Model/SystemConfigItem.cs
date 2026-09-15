namespace InkPrinterCode.Model
{
    /// <summary>
    /// [2026-09-10] System parameter configuration item entity (maps to the SystemConfig table, key/value pairs)
    ///
    /// [Purpose] The display and save carrier for the system parameter configuration form -- every adjustable
    ///   parameter on the page (heartbeat interval, timeouts, retry counts, page size, etc.) corresponds to one record.
    ///
    /// [Field defaults] Every field has a default (development convention); null is not allowed.
    /// </summary>
    public class SystemConfigItem
    {
        /// <summary>Parameter key name (primary key, e.g. HeartbeatIntervalMs)</summary>
        public string ConfigKey { get; set; } = string.Empty;

        /// <summary>Parameter value (stored uniformly as a string; the reader converts by type)</summary>
        public string ConfigValue { get; set; } = string.Empty;

        /// <summary>Chinese description (for DB inspection and troubleshooting; not shown on the page)</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Last update time (TEXT, yyyy-MM-dd HH:mm:ss)</summary>
        public string UpdateTime { get; set; } = string.Empty;
    }
}

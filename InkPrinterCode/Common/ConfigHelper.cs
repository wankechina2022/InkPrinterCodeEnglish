namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Configuration helper class (v2 —— the version in which the config file was removed entirely)
    ///
    /// [Background] v1 read parameters from App.config's appSettings. A previous project suffered an
    ///   incident in which "writing back to the config file corrupted its XML and the program could never start again",
    ///   so on 2026-09-10 it was decided: do not use a config file at all. Parameters are instead stored in the
    ///   database's SystemConfig table (a SQLite transaction write is atomic, so there is no half-written state), and
    ///   App.config together with its NuGet package (System.Configuration.ConfigurationManager) was removed as well.
    ///
    /// [Only two configuration sources remain]
    ///   1. Built-in defaults (the constants in this class / GetDefaultValues, the lowest-level fallback);
    ///   2. The SystemConfig table (database; the only adjustable source, maintained by the system-parameter
    ///      configuration form).
    ///
    /// [Mechanism]
    ///   - At program startup SystemConfigDAL pours the whole table's key/value pairs into this class's cache through LoadCache;
    ///   - After the configuration form is saved successfully, SystemConfigBLL calls UpdateCache to refresh it —— the
    ///     change takes effect immediately, with no restart needed;
    ///   - Property reads = use the cached value if present (with range-validity checking), otherwise fall back to the
    ///     built-in default when there is no value or it is out of range;
    ///   - The cache is locked: the configuration form saving (UI thread) and background threads reading parameters
    ///     may be concurrent.
    ///
    /// [Layering note] This class is in the Common layer and does not reference DAL/BLL; instead the DAL layer actively
    ///   "pours data in" (DAL referencing Common is legal, whereas Common referencing DAL back would break the
    ///   one-way dependency).
    ///
    /// [Boundaries] This class does not write logs —— LogHelper itself needs to read this class's configuration, and a
    ///   reverse call would create a circular initialization dependency; exceptions are merely swallowed silently
    ///   (Debug.WriteLine).
    /// </summary>
    public static class ConfigHelper
    {
        // ============================================================
        // Built-in constants (technical parameters that do not go into the database or onto the configuration page)
        // ============================================================

        /// <summary>Name of the folder holding the database (relative to the program directory)</summary>
        private const string DB_FOLDER_NAME = "Data";

        /// <summary>Database file name</summary>
        private const string DB_FILE_NAME = "InkPrinterCode.db";

        /// <summary>Number of records per batch for bulk inserts (an internal tuning parameter, hard-coded)</summary>
        public const int IMPORT_BATCH_SIZE = 1000;

        /// <summary>Progress reporting step: report once every this many records processed (an internal tuning parameter, hard-coded)</summary>
        public const int PROGRESS_REPORT_STEP = 1000;

        /// <summary>
        /// Number of parameters per batch for SQL IN queries / bulk deletes by primary key
        /// [Boundaries] SQLite's limit is 999 parameters per statement; 500 leaves ample margin (hard-coded).
        /// </summary>
        public const int SQL_IN_PARAM_BATCH_SIZE = 500;

        // ============================================================
        // Parameter key-name constants (ConfigKey of the SystemConfig table; defined centrally to avoid scattered strings)
        // ============================================================

        public const string KEY_PAGE_SIZE = "PageSize";
        public const string KEY_LOG_KEEP_DAYS = "LogKeepDays";
        public const string KEY_DUPLICATE_LOG_LIMIT = "DuplicateLogLimit";
        public const string KEY_RUN_LOG_MAX_LINES = "RunLogMaxLines";
        public const string KEY_ENABLE_LOGGING = "EnableLogging";
        public const string KEY_MIN_LOG_LEVEL = "MinLogLevel";
        public const string KEY_DEDUP_HASHSET_THRESHOLD = "DedupHashSetThreshold";
        public const string KEY_HEARTBEAT_INTERVAL_MS = "HeartbeatIntervalMs";
        public const string KEY_SEND_RESPONSE_TIMEOUT_MS = "SendResponseTimeoutMs";
        public const string KEY_HEARTBEAT_TIMEOUT_MS = "HeartbeatTimeoutMs";
        public const string KEY_RECONNECT_INTERVAL_MS = "ReconnectIntervalMs";
        public const string KEY_INITIAL_CACHE_COUNT = "InitialCacheCount";
        public const string KEY_MAX_RETRY_COUNT = "MaxRetryCount";
        public const string KEY_TCP_RESET_ENABLED = "TcpResetEnabled";

        // ============================================================
        // Cache (the in-memory mirror of the SystemConfig table; key = ConfigKey, value = the raw ConfigValue string)
        // ============================================================

        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Pour the entire SystemConfig table's key/value pairs into the cache (called once at program startup by SystemConfigDAL)
        /// [Notes] Passing null or an empty dictionary is not treated as an error —— in that case all parameters use the
        /// built-in defaults.
        /// </summary>
        public static void LoadCache(Dictionary<string, string>? values)
        {
            lock (_cacheLock)
            {
                _cache.Clear();
                if (values != null)
                {
                    foreach (KeyValuePair<string, string> pair in values)
                    {
                        _cache[pair.Key] = pair.Value;
                    }
                }
            }
        }

        /// <summary>
        /// Refresh a single cache entry (called by SystemConfigBLL after the config form saves successfully; takes effect immediately)
        /// </summary>
        public static void UpdateCache(string key, string value)
        {
            lock (_cacheLock)
            {
                _cache[key] = value;
            }
        }

        /// <summary>
        /// Get the list of built-in defaults (14 adjustable parameters) —— used by SystemConfigDAL to seed a
        /// newly-created database
        /// [Notes] It returns only key → default value; the Chinese descriptions are maintained by SystemConfigDAL
        /// (keeping the Common layer free of a reference to Model).
        /// </summary>
        public static Dictionary<string, string> GetDefaultValues()
        {
            Dictionary<string, string> defaults = new Dictionary<string, string>();

            // ---------- Data & logging group ----------
            defaults[KEY_PAGE_SIZE] = "100";
            defaults[KEY_LOG_KEEP_DAYS] = "30";
            defaults[KEY_DUPLICATE_LOG_LIMIT] = "500";
            defaults[KEY_RUN_LOG_MAX_LINES] = "100";
            defaults[KEY_ENABLE_LOGGING] = "true";
            defaults[KEY_MIN_LOG_LEVEL] = "INFO";
            defaults[KEY_DEDUP_HASHSET_THRESHOLD] = "1000000";

            // ---------- Inkjet communication group ----------
            defaults[KEY_HEARTBEAT_INTERVAL_MS] = "3000";
            defaults[KEY_SEND_RESPONSE_TIMEOUT_MS] = "3000";
            defaults[KEY_HEARTBEAT_TIMEOUT_MS] = "2000";
            defaults[KEY_RECONNECT_INTERVAL_MS] = "5000";
            defaults[KEY_INITIAL_CACHE_COUNT] = "3";
            defaults[KEY_MAX_RETRY_COUNT] = "3";
            // [2026-09-11] The port-700 pre-reset feature is abandoned entirely —— so the default
            //   is changed to false (unchecked). The UI checkbox has been hidden, saving always writes false, and
            //   "restore defaults" also returns false (this dictionary also feeds the restore-defaults button), so all
            //   four paths converge.
            defaults[KEY_TCP_RESET_ENABLED] = "false";

            return defaults;
        }

        // ============================================================
        // Database-related (fixed constants, not configurable)
        // ============================================================

        /// <summary>Full path of the folder holding the database (program directory + Data)</summary>
        public static string DbFolderPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DB_FOLDER_NAME); }
        }

        /// <summary>Full path of the database file</summary>
        public static string DbFilePath
        {
            get { return Path.Combine(DbFolderPath, DB_FILE_NAME); }
        }

        /// <summary>
        /// SQLite connection string
        /// [Notes] The Microsoft.Data.Sqlite connection string is simple: Data Source only needs to point at the db file;
        ///   If the file does not exist it is created automatically (the directory must exist first; DbInitializer is responsible for creating it).
        /// </summary>
        public static string ConnectionString
        {
            // [2026-09-16] P1: busy handling for concurrent writers.
            // [2026-09-17 fix] Final answer: Microsoft.Data.Sqlite supports NO busy-timeout
            //   knob at all — not a connection-string keyword, not a property (verified
            //   against the 8.0.31 assembly). It retries SQLITE_BUSY internally until
            //   DefaultTimeout (30 s default). The connection string stays plain.
            get { return "Data Source=" + DbFilePath + ";"; }
        }

        // ============================================================
        // UI-related (adjustable via the SystemConfig table)
        // ============================================================

        /// <summary>Records per page on the data view page; default 100, allowed range 10~1000</summary>
        public static int PageSize
        {
            get { return GetInt(KEY_PAGE_SIZE, 100, 10, 1000); }
        }

        // ============================================================
        // Logging-related (adjustable via the SystemConfig table)
        // ============================================================

        /// <summary>Master logging switch, enabled by default</summary>
        public static bool EnableLogging
        {
            get { return GetBool(KEY_ENABLE_LOGGING, true); }
        }

        /// <summary>
        /// Minimum log level text (DEBUG/INFO/WARN/ERROR/FATAL), default INFO
        /// [Notes] It deliberately returns a string rather than the LogHelper.LogLevel enum —— this avoids
        ///   ConfigHelper and LogHelper referencing each other's types and creating coupling; the parsing is done
        ///   inside LogHelper.
        /// </summary>
        public static string MinLogLevelText
        {
            get { return GetString(KEY_MIN_LOG_LEVEL, "INFO"); }
        }

        /// <summary>Log retention in days, default 30; 0 means never clean up</summary>
        public static int LogKeepDays
        {
            get { return GetInt(KEY_LOG_KEEP_DAYS, 30, 0, 365); }
        }

        /// <summary>Upper limit on duplicate-code detail log entries written per import, default 500</summary>
        public static int DuplicateLogLimit
        {
            get { return GetInt(KEY_DUPLICATE_LOG_LIMIT, 500, 100, 5000); }
        }

        /// <summary>Maximum number of lines in the main window run log, default 100 (used to trim txtRunLog in stage two)</summary>
        public static int RunLogMaxLines
        {
            get { return GetInt(KEY_RUN_LOG_MAX_LINES, 100, 10, 1000); }
        }

        // ============================================================
        // Import performance-related
        // ============================================================

        /// <summary>
        /// Deduplication dual-path threshold, default 1 million
        /// [Mechanism] When the total number of CodeData records in the database is &lt;= this value, all code values in
        ///   the database are read into an in-memory HashSet at once for deduplication (the fastest way); above this
        ///   value it switches to batched IN queries, trading a little speed for memory safety.
        /// </summary>
        public static int DedupHashSetThreshold
        {
            get { return GetInt(KEY_DEDUP_HASHSET_THRESHOLD, 1000000, 10000, 100000000); }
        }

        /// <summary>Number of records per bulk-insert batch (built-in constant 1000, hard-coded)</summary>
        public static int ImportBatchSize
        {
            get { return IMPORT_BATCH_SIZE; }
        }

        /// <summary>Progress reporting step (built-in constant 1000, hard-coded)</summary>
        public static int ProgressReportStep
        {
            get { return PROGRESS_REPORT_STEP; }
        }

        /// <summary>Number of parameters per SQL IN query batch (built-in constant 500, hard-coded)</summary>
        public static int SqlInParamBatchSize
        {
            get { return SQL_IN_PARAM_BATCH_SIZE; }
        }

        // ============================================================
        // Inkjet communication-related (adjustable via the SystemConfig table; added with the printing feature)
        // ============================================================

        /// <summary>
        /// Heartbeat period in milliseconds, default 3000, allowed 500~600000
        /// [2026-09-10] The heartbeat has no on/off switch and must always be on —— in this project
        ///   the heartbeat (the query-status command) serves both as a liveness check and as the only means of
        ///   detecting a disconnection, so switching it off would make disconnections undetectable. The standard's
        ///   "heartbeat mechanism has a switch" item is deliberately omitted in this project, which is a documented
        ///   standard deviation (see the phase-two audit report).
        /// </summary>
        public static int HeartbeatIntervalMs
        {
            get { return GetInt(KEY_HEARTBEAT_INTERVAL_MS, 3000, 500, 600000); }
        }

        /// <summary>
        /// Command reply timeout in milliseconds, default 3000, allowed 500~60000.
        /// [Applicable to] Only control commands such as "print signal setup / clear the three queues (startup and
        ///   reconnect)" —— these must be given ample reply time.
        /// [2026-09-10] [Not applicable to] Production code sending (SendOneCode) has switched to
        ///   PrintServiceBLL.SEND_ACK_TIMEOUT_MS (a 100 ms constant), the goal being to shorten how long the business
        ///   lock is held and avoid a long transaction blocking the heartbeat and causing a false disconnection
        ///   determination; this configuration item no longer participates in the code-sending path.
        /// </summary>
        public static int SendResponseTimeoutMs
        {
            get { return GetInt(KEY_SEND_RESPONSE_TIMEOUT_MS, 3000, 500, 60000); }
        }

        /// <summary>Heartbeat reply timeout in milliseconds, default 2000, allowed 200~30000</summary>
        public static int HeartbeatTimeoutMs
        {
            get { return GetInt(KEY_HEARTBEAT_TIMEOUT_MS, 2000, 200, 30000); }
        }

        /// <summary>Interval in milliseconds for automatic reconnection after a disconnection, default 5000, allowed 1000~600000</summary>
        public static int ReconnectIntervalMs
        {
            get { return GetInt(KEY_RECONNECT_INTERVAL_MS, 5000, 1000, 600000); }
        }

        /// <summary>Number of entries used to pre-fill the inkjet printer's cache, default 3, allowed 1~20</summary>
        public static int InitialCacheCount
        {
            get { return GetInt(KEY_INITIAL_CACHE_COUNT, 3, 1, 20); }
        }

        /// <summary>
        /// Upper limit on retries for a failed single-code send, default 3, allowed 0~10.
        /// [2026-09-10] Disabled (the configuration item and table record are kept; do not delete): under the
        ///   code-claim occupancy model, "a code is written only once and never retried" has been finalized (decided on
        ///   2026-09-10 at 17:29), since a retry amounts to writing the same code into the inkjet printer a second time.
        ///   This item currently participates in no business logic; it is retained only for reuse should the retry
        ///   mechanism ever be restored.
        /// </summary>
        public static int MaxRetryCount
        {
            get { return GetInt(KEY_MAX_RETRY_COUNT, 3, 0, 10); }
        }

        /// <summary>Whether to pre-reset port 700 before opening a TCP connection.
        /// [2026-09-11] The feature is abandoned and the default is false (unchecked) —— the seed
        /// default, the save submission and the restore-defaults path are all changed to false as well.</summary>
        public static bool TcpResetEnabled
        {
            get { return GetBool(KEY_TCP_RESET_ENABLED, false); }
        }

        // ============================================================
        // Miscellaneous
        // ============================================================

        /// <summary>
        /// Operator name
        /// [Fallback] Takes the current Windows login user name; returns "unknown" if it cannot be obtained.
        /// </summary>
        public static string OperatorName
        {
            get
            {
                try
                {
                    string userName = Environment.UserName;
                    if (string.IsNullOrWhiteSpace(userName))
                    {
                        return "unknown";
                    }
                    return userName;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("ConfigHelper failed to read the current user name: " + ex.Message);
                    return "unknown";
                }
            }
        }

        // ============================================================
        // Private value getters (the unified fallback path: cache → range validation → built-in default)
        // ============================================================

        /// <summary>Get the raw string from the cache (locked, to prevent concurrency between the configuration form saving and background reads)</summary>
        private static string? GetRawValue(string key)
        {
            lock (_cacheLock)
            {
                string? value = null;
                _cache.TryGetValue(key, out value);
                return value;
            }
        }

        /// <summary>Read a string setting: falls back to the default when the cache has no value or it is empty</summary>
        private static string GetString(string key, string defaultValue)
        {
            try
            {
                string? value = GetRawValue(key);
                if (string.IsNullOrWhiteSpace(value))
                {
                    return defaultValue;
                }
                return value.Trim();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ConfigHelper failed to read configuration [" + key + "]: " + ex.Message);
                return defaultValue;
            }
        }

        /// <summary>Read an integer setting, clamped into the range [minValue, maxValue] (out of range falls back to the default)</summary>
        private static int GetInt(string key, int defaultValue, int minValue, int maxValue)
        {
            try
            {
                string? text = GetRawValue(key);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return defaultValue;
                }

                int result = 0;
                if (!int.TryParse(text.Trim(), out result))
                {
                    return defaultValue;
                }

                if (result < minValue || result > maxValue)
                {
                    return defaultValue;
                }
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ConfigHelper failed to read configuration [" + key + "]: " + ex.Message);
                return defaultValue;
            }
        }

        /// <summary>Read a boolean setting. Supports true/false and is also compatible with the 1/0 and yes/no forms</summary>
        private static bool GetBool(string key, bool defaultValue)
        {
            try
            {
                string? text = GetRawValue(key);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return defaultValue;
                }

                string trimmed = text.Trim().ToLowerInvariant();
                if (trimmed == "true" || trimmed == "1" || trimmed == "yes")
                {
                    return true;
                }
                if (trimmed == "false" || trimmed == "0" || trimmed == "no")
                {
                    return false;
                }
                return defaultValue;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ConfigHelper failed to read configuration [" + key + "]: " + ex.Message);
                return defaultValue;
            }
        }
    }
}

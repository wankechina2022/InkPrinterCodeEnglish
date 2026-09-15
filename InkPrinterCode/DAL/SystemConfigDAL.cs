using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] System parameter config data access class (SystemConfig table, key-value pairs)
    ///
    /// [Responsibilities] Seed the built-in defaults, read everything (so ConfigHelper can populate its cache) and save everything (from the config form).
    ///
    /// [Three non-negotiable rules]
    ///   1. Only CREATE TABLE IF NOT EXISTS is used (table creation lives in DbInitializer); this class never changes table structures;
    ///   2. Fully parameterised; string-concatenated values are forbidden;
    ///   3. A full save commits inside a single transaction -- either everything succeeds or nothing changes, never half new and half old.
    ///
    /// [Layering] DAL references Common (ConfigHelper provides defaults and key names) and Model (entities); this dependency direction is valid.
    /// </summary>
    public static class SystemConfigDAL
    {
        /// <summary>Descriptions of each parameter (written into the Description column when seeding, for back-end troubleshooting via the database)</summary>
        private static readonly Dictionary<string, string> _descriptions = BuildDescriptions();

        // ============================================================
        // 1. Seeding
        // ============================================================

        /// <summary>
        /// Seed the built-in default parameters (INSERT OR IGNORE: existing keys are never overwritten -- this protects values the user has already changed)
        /// </summary>
        /// <returns>Number of rows actually inserted this time (0 = all already existed)</returns>
        public static int SeedDefaults()
        {
            Dictionary<string, string> defaults = ConfigHelper.GetDefaultValues();
            int inserted = 0;

            foreach (KeyValuePair<string, string> pair in defaults)
            {
                string sql = @"INSERT OR IGNORE INTO SystemConfig (ConfigKey, ConfigValue, Description, UpdateTime)
                               VALUES (@Key, @Value, @Description, @UpdateTime);";

                SqliteParameter[] parameters = new SqliteParameter[]
                {
                    SqliteHelper.CreateParameter("@Key", pair.Key),
                    SqliteHelper.CreateParameter("@Value", pair.Value),
                    SqliteHelper.CreateParameter("@Description", GetDescription(pair.Key)),
                    SqliteHelper.CreateParameter("@UpdateTime", Extensions.ToDbTimeString(DateTime.Now))
                };

                int affected = SqliteHelper.ExecuteNonQuery(sql, parameters);
                inserted += affected;
            }

            return inserted;
        }

        // ============================================================
        // 2. Reading
        // ============================================================

        /// <summary>
        /// Read all parameters (key → value), for ConfigHelper.LoadCache to load into its cache
        /// [Notes] SELECT * is not used (red line); only the two needed columns are taken.
        /// </summary>
        public static Dictionary<string, string> LoadAll()
        {
            Dictionary<string, string> values = new Dictionary<string, string>();

            string sql = "SELECT ConfigKey, ConfigValue FROM SystemConfig;";
            DataTable table = SqliteHelper.ExecuteQuery(sql);

            foreach (DataRow row in table.Rows)
            {
                string key = row["ConfigKey"].ToSafeString();
                string value = row["ConfigValue"].ToSafeString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    values[key] = value;
                }
            }

            return values;
        }

        // ============================================================
        // 3. Save (full overwrite in a single transaction)
        // ============================================================

        /// <summary>
        /// Save the full parameter list (single transaction; all succeed or nothing changes)
        /// [Notes] Uses INSERT OR REPLACE (SQLite's upsert form) --
        ///   if the key exists the whole row is replaced (ConfigValue / Description / UpdateTime are all updated), otherwise it is inserted.
        /// </summary>
        /// <param name="items">Parameter list to save (from the config form, already validated by the BLL)</param>
        /// <returns>Number of rows saved successfully (a failure rolls everything back and throws, which the BLL catches)</returns>
        public static int SaveAll(List<SystemConfigItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return 0;
            }

            int saved = 0;

            SqliteHelper.ExecuteInTransaction(delegate(SqliteConnection connection, SqliteTransaction transaction)
            {
                const string sql = @"INSERT OR REPLACE INTO SystemConfig (ConfigKey, ConfigValue, Description, UpdateTime)
                                     VALUES (@Key, @Value, @Description, @UpdateTime);";

                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = sql;

                    for (int i = 0; i < items.Count; i++)
                    {
                        command.Parameters.Clear();
                        command.Parameters.Add(SqliteHelper.CreateParameter("@Key", items[i].ConfigKey));
                        command.Parameters.Add(SqliteHelper.CreateParameter("@Value", items[i].ConfigValue));
                        command.Parameters.Add(SqliteHelper.CreateParameter("@Description", GetDescription(items[i].ConfigKey)));
                        command.Parameters.Add(SqliteHelper.CreateParameter("@UpdateTime", Extensions.ToDbTimeString(DateTime.Now)));

                        saved += command.ExecuteNonQuery();
                    }
                }
            });

            return saved;
        }

        // ============================================================
        // Private helpers
        // ============================================================

        /// <summary>Get a parameter's description (returns an empty string for keys not in the description list)</summary>
        private static string GetDescription(string key)
        {
            string description = string.Empty;
            _descriptions.TryGetValue(key, out description);
            return description;
        }

        /// <summary>Build the description list (the key names correspond one-to-one with ConfigHelper.KEY_*)</summary>
        private static Dictionary<string, string> BuildDescriptions()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();

            map[ConfigHelper.KEY_PAGE_SIZE] = "Records per page on the data view page (10~1000)";
            map[ConfigHelper.KEY_LOG_KEEP_DAYS] = "Log retention in days, 0=never clean up (0~365)";
            map[ConfigHelper.KEY_DUPLICATE_LOG_LIMIT] = "Upper limit on duplicate-code detail log entries per import (100~5000)";
            map[ConfigHelper.KEY_RUN_LOG_MAX_LINES] = "Maximum number of lines in the main form's runtime log; automatically truncated beyond that (10~1000)";
            map[ConfigHelper.KEY_ENABLE_LOGGING] = "Master logging switch (false = keep only the error copy)";
            map[ConfigHelper.KEY_MIN_LOG_LEVEL] = "Minimum log level (DEBUG/INFO/WARN/ERROR/FATAL)";
            map[ConfigHelper.KEY_DEDUP_HASHSET_THRESHOLD] = "In-memory HashSet threshold for deduplication; above this it switches to batched IN queries (10000~100000000)";
            map[ConfigHelper.KEY_HEARTBEAT_INTERVAL_MS] = "Heartbeat period in milliseconds (500~600000); must be greater than the heartbeat timeout";
            map[ConfigHelper.KEY_SEND_RESPONSE_TIMEOUT_MS] = "Timeout in milliseconds for waiting for the 0x06 reply to a command/data send (500~60000)";
            map[ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS] = "Heartbeat reply timeout in milliseconds (200~30000); must be less than the heartbeat period";
            map[ConfigHelper.KEY_RECONNECT_INTERVAL_MS] = "Interval in milliseconds for automatic reconnection after a disconnection (1000~600000)";
            map[ConfigHelper.KEY_INITIAL_CACHE_COUNT] = "Number of entries used to pre-fill the inkjet printer's cache (1~20)";
            map[ConfigHelper.KEY_MAX_RETRY_COUNT] = "Upper limit on retries for a failed single-code send (0~10)";
            map[ConfigHelper.KEY_TCP_RESET_ENABLED] = "Pre-reset port 700 before opening a TCP connection (field experience; can be disabled)";

            return map;
        }
    }
}

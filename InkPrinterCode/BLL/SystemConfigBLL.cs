using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-10] System parameter configuration business class (middle layer between the config form, the SystemConfig table and the ConfigHelper cache)
    ///
    /// [Responsibilities]
    ///   1. Load: read all parameters from the table (missing keys are filled with built-in defaults) and assemble a list the form can display;
    ///   2. Validate: per-item range validation + cross validation (the heartbeat timeout must be less than the heartbeat interval);
    ///   3. Save: write to the database in a single transaction -> refresh the ConfigHelper cache -> refresh the log level -- effective immediately, no restart.
    ///
    /// [Validation principle] If any single item is invalid the whole batch is refused (a specific reason is returned and the form focuses the offending item);
    ///   never "save the valid ones and skip the invalid ones" -- that would make users believe everything was saved.
    /// </summary>
    public static class SystemConfigBLL
    {
        /// <summary>Validation result: when Success=true, Items is the savable list; when false, Message is the first violation reason</summary>
        public class ValidateResult
        {
            public bool Success { get; set; } = false;
            public string Message { get; set; } = string.Empty;
            /// <summary>The complete parameter list after validation passes (used for saving)</summary>
            public List<SystemConfigItem> Items = new List<SystemConfigItem>();
            /// <summary>The key name of the offending parameter (used by the form to focus the right control)</summary>
            public string InvalidKey { get; set; } = string.Empty;
        }

        // ============================================================
        // 1. Load
        // ============================================================

        /// <summary>
        /// Load all parameters (use the database value when present, fill in missing keys with the built-in defaults)
        /// [Note] If reading the database fails, fall back to the pure built-in default list, so the config form can always be opened.
        /// </summary>
        public static List<SystemConfigItem> LoadItems()
        {
            Dictionary<string, string> dbValues = new Dictionary<string, string>();

            try
            {
                dbValues = SystemConfigDAL.LoadAll();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to read system parameters (built-in defaults will be shown)", ex);
            }

            List<SystemConfigItem> items = new List<SystemConfigItem>();

            foreach (KeyValuePair<string, string> pair in ConfigHelper.GetDefaultValues())
            {
                SystemConfigItem item = new SystemConfigItem();
                item.ConfigKey = pair.Key;
                item.ConfigValue = pair.Value;

                string? dbValue;
                if (dbValues.TryGetValue(pair.Key, out dbValue) && !string.IsNullOrWhiteSpace(dbValue))
                {
                    item.ConfigValue = dbValue.Trim();
                }

                items.Add(item);
            }

            return items;
        }

        // ============================================================
        // 2. Validation
        // ============================================================

        /// <summary>
        /// Validate parameter values (validated from the key -> raw text dictionary submitted by the form)
        /// [Per-item rules] See the ranges at each ValidateInt call site; [cross rule] HeartbeatTimeoutMs &lt; HeartbeatIntervalMs.
        /// </summary>
        /// <param name="inputValues">Key -> the raw text entered by the user</param>
        public static ValidateResult Validate(Dictionary<string, string> inputValues)
        {
            ValidateResult result = new ValidateResult();

            if (inputValues == null)
            {
                result.Message = "The parameter data is invalid";
                return result;
            }

            // ---------- Per-item range validation ----------
            ValidateIntResult pageSize = ValidateInt(inputValues, ConfigHelper.KEY_PAGE_SIZE, 10, 1000);
            if (!pageSize.Ok) { result.Message = pageSize.Message; result.InvalidKey = pageSize.Key; return result; }

            ValidateIntResult keepDays = ValidateInt(inputValues, ConfigHelper.KEY_LOG_KEEP_DAYS, 0, 365);
            if (!keepDays.Ok) { result.Message = keepDays.Message; result.InvalidKey = keepDays.Key; return result; }

            ValidateIntResult dupLimit = ValidateInt(inputValues, ConfigHelper.KEY_DUPLICATE_LOG_LIMIT, 100, 5000);
            if (!dupLimit.Ok) { result.Message = dupLimit.Message; result.InvalidKey = dupLimit.Key; return result; }

            ValidateIntResult runLogLines = ValidateInt(inputValues, ConfigHelper.KEY_RUN_LOG_MAX_LINES, 10, 1000);
            if (!runLogLines.Ok) { result.Message = runLogLines.Message; result.InvalidKey = runLogLines.Key; return result; }

            ValidateIntResult dedupThreshold = ValidateInt(inputValues, ConfigHelper.KEY_DEDUP_HASHSET_THRESHOLD, 10000, 100000000);
            if (!dedupThreshold.Ok) { result.Message = dedupThreshold.Message; result.InvalidKey = dedupThreshold.Key; return result; }

            ValidateIntResult heartbeatInterval = ValidateInt(inputValues, ConfigHelper.KEY_HEARTBEAT_INTERVAL_MS, 500, 600000);
            if (!heartbeatInterval.Ok) { result.Message = heartbeatInterval.Message; result.InvalidKey = heartbeatInterval.Key; return result; }

            ValidateIntResult sendTimeout = ValidateInt(inputValues, ConfigHelper.KEY_SEND_RESPONSE_TIMEOUT_MS, 500, 60000);
            if (!sendTimeout.Ok) { result.Message = sendTimeout.Message; result.InvalidKey = sendTimeout.Key; return result; }

            ValidateIntResult heartbeatTimeout = ValidateInt(inputValues, ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS, 200, 30000);
            if (!heartbeatTimeout.Ok) { result.Message = heartbeatTimeout.Message; result.InvalidKey = heartbeatTimeout.Key; return result; }

            ValidateIntResult reconnectInterval = ValidateInt(inputValues, ConfigHelper.KEY_RECONNECT_INTERVAL_MS, 1000, 600000);
            if (!reconnectInterval.Ok) { result.Message = reconnectInterval.Message; result.InvalidKey = reconnectInterval.Key; return result; }

            ValidateIntResult cacheCount = ValidateInt(inputValues, ConfigHelper.KEY_INITIAL_CACHE_COUNT, 1, 20);
            if (!cacheCount.Ok) { result.Message = cacheCount.Message; result.InvalidKey = cacheCount.Key; return result; }

            ValidateIntResult maxRetry = ValidateInt(inputValues, ConfigHelper.KEY_MAX_RETRY_COUNT, 0, 10);
            if (!maxRetry.Ok) { result.Message = maxRetry.Message; result.InvalidKey = maxRetry.Key; return result; }

            // ---------- Enum/boolean validation ----------
            string minLevel = GetString(inputValues, ConfigHelper.KEY_MIN_LOG_LEVEL, "INFO").ToUpperInvariant();
            if (minLevel != "DEBUG" && minLevel != "INFO" && minLevel != "WARN" && minLevel != "ERROR" && minLevel != "FATAL")
            {
                result.Message = "The minimum log level must be one of DEBUG / INFO / WARN / ERROR / FATAL";
                result.InvalidKey = ConfigHelper.KEY_MIN_LOG_LEVEL;
                return result;
            }

            // ---------- Cross validation: the heartbeat timeout must be less than the heartbeat interval ----------
            if (heartbeatTimeout.Value >= heartbeatInterval.Value)
            {
                result.Message = "Cross validation failed: the heartbeat response timeout (" + heartbeatTimeout.Value
                                 + "ms) must be less than the heartbeat interval (" + heartbeatInterval.Value
                                 + "ms), otherwise the heartbeat would always time out before a response could arrive";
                result.InvalidKey = ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS;
                return result;
            }

            // ---------- All passed: assemble the save list ----------
            List<SystemConfigItem> items = new List<SystemConfigItem>();
            AddItem(items, inputValues, ConfigHelper.KEY_PAGE_SIZE, pageSize.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_LOG_KEEP_DAYS, keepDays.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_DUPLICATE_LOG_LIMIT, dupLimit.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_RUN_LOG_MAX_LINES, runLogLines.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_ENABLE_LOGGING, GetBool(inputValues, ConfigHelper.KEY_ENABLE_LOGGING, true) ? "true" : "false");
            AddItem(items, inputValues, ConfigHelper.KEY_MIN_LOG_LEVEL, minLevel);
            AddItem(items, inputValues, ConfigHelper.KEY_DEDUP_HASHSET_THRESHOLD, dedupThreshold.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_HEARTBEAT_INTERVAL_MS, heartbeatInterval.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_SEND_RESPONSE_TIMEOUT_MS, sendTimeout.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS, heartbeatTimeout.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_RECONNECT_INTERVAL_MS, reconnectInterval.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_INITIAL_CACHE_COUNT, cacheCount.Value.ToString());
            AddItem(items, inputValues, ConfigHelper.KEY_MAX_RETRY_COUNT, maxRetry.Value.ToString());
            // [2026-09-11] Instruction from Mr. Wan: the 700 pre-reset is deprecated -- the fallback default was changed from true to false;
            //   and the form always submits "false", so in practice "false" is always stored here. The fallback only guards against
            //   inputValues missing the key in extreme cases.
            AddItem(items, inputValues, ConfigHelper.KEY_TCP_RESET_ENABLED, GetBool(inputValues, ConfigHelper.KEY_TCP_RESET_ENABLED, false) ? "true" : "false");

            result.Success = true;
            result.Items = items;
            return result;
        }

        // ============================================================
        // 3. Save
        // ============================================================

        /// <summary>
        /// Save the parameters (validate first → write to the database in a single transaction → refresh the ConfigHelper cache → refresh the log level)
        /// [Effective immediately] Parameters such as heartbeat/timeout take effect on their next usage cycle; MinLogLevel resets the LogHelper
        /// cache and takes effect immediately.
        /// </summary>
        public static ValidateResult Save(Dictionary<string, string> inputValues)
        {
            // 1. Validate first
            ValidateResult validate = Validate(inputValues);
            if (!validate.Success)
            {
                return validate;
            }

            // 2. Write to the database in a single transaction
            try
            {
                SystemConfigDAL.SaveAll(validate.Items);
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to save system parameters", ex);
                validate.Success = false;
                validate.Message = "Save failed: " + ex.Message;
                return validate;
            }

            // 3. Refresh the in-memory cache (the key step for "effective immediately on save")
            foreach (SystemConfigItem item in validate.Items)
            {
                ConfigHelper.UpdateCache(item.ConfigKey, item.ConfigValue);
            }

            // 4. Log level changes take effect immediately (reset the LogHelper parsing cache)
            LogHelper.Instance.RefreshMinLevel();

            LogHelper.Instance.Info("System parameters saved successfully, " + validate.Items.Count + " items in total, effective immediately");
            return validate;
        }

        /// <summary>
        /// Restore all default values (the config form's "Restore Defaults" button: write to the database + refresh the cache + refresh the log level)
        /// </summary>
        /// <returns>Returns true on success; on failure Message carries the reason</returns>
        public static bool ResetToDefaults(out string message)
        {
            message = string.Empty;

            try
            {
                Dictionary<string, string> defaults = ConfigHelper.GetDefaultValues();
                List<SystemConfigItem> items = new List<SystemConfigItem>();

                foreach (KeyValuePair<string, string> pair in defaults)
                {
                    items.Add(new SystemConfigItem
                    {
                        ConfigKey = pair.Key,
                        ConfigValue = pair.Value
                    });
                }

                SystemConfigDAL.SaveAll(items);

                foreach (SystemConfigItem item in items)
                {
                    ConfigHelper.UpdateCache(item.ConfigKey, item.ConfigValue);
                }

                LogHelper.Instance.RefreshMinLevel();
                LogHelper.Instance.Info("All system parameters have been restored to their default values (" + items.Count + " items)");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to restore default parameters", ex);
                message = ex.Message;
                return false;
            }
        }

        // ============================================================
        // Private validation helpers
        // ============================================================

        private class ValidateIntResult
        {
            public bool Ok = false;
            public int Value = 0;
            public string Key = string.Empty;
            public string Message = string.Empty;
        }

        /// <summary>Integer validation: must be convertible to an integer and fall within the [min, max] range</summary>
        private static ValidateIntResult ValidateInt(Dictionary<string, string> inputValues, string key, int min, int max)
        {
            ValidateIntResult result = new ValidateIntResult();
            result.Key = key;

            string raw = string.Empty;
            if (!inputValues.TryGetValue(key, out raw) || string.IsNullOrWhiteSpace(raw))
            {
                result.Message = "Parameter " + key + " cannot be empty";
                return result;
            }

            int value = 0;
            if (!int.TryParse(raw.Trim(), out value))
            {
                result.Message = "Parameter " + key + " must be an integer; current input: " + raw.Trim();
                return result;
            }

            if (value < min || value > max)
            {
                result.Message = "Parameter " + key + " must be between " + min.ToString() + " and " + max.ToString() + "; current input: " + value.ToString();
                return result;
            }

            result.Ok = true;
            result.Value = value;
            return result;
        }

        /// <summary>Get a string input (falls back to the default when there is no input)</summary>
        private static string GetString(Dictionary<string, string> inputValues, string key, string defaultValue)
        {
            string raw = string.Empty;
            if (inputValues.TryGetValue(key, out raw) && !string.IsNullOrWhiteSpace(raw))
            {
                return raw.Trim();
            }
            return defaultValue;
        }

        /// <summary>Get a boolean input (true/false/1/0/yes/no; anything else falls back to the default)</summary>
        private static bool GetBool(Dictionary<string, string> inputValues, string key, bool defaultValue)
        {
            string raw = string.Empty;
            if (!inputValues.TryGetValue(key, out raw) || string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            string trimmed = raw.Trim().ToLowerInvariant();
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

        /// <summary>Add the validated key/value to the save list (when the input is missing, the current/default value is used so the list stays complete)</summary>
        private static void AddItem(List<SystemConfigItem> items, Dictionary<string, string> inputValues, string key, string value)
        {
            items.Add(new SystemConfigItem
            {
                ConfigKey = key,
                ConfigValue = value
            });
        }
    }
}

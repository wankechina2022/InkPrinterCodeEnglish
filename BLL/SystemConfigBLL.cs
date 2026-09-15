using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Model;

namespace InkPrinterCode.BLL
{
    /// <summary>
    /// [2026-09-10] 系统参数配置业务类（配置窗体 ↔ SystemConfig 表 ↔ ConfigHelper 缓存 的中间层）
    ///
    /// 【职责】
    ///   1. 加载：从表读出全部参数（缺键用内置默认补齐），组装成窗体可显示的清单；
    ///   2. 校验：逐项范围校验 + 交叉校验（心跳超时必须小于心跳周期）；
    ///   3. 保存：单事务写库 → 刷新 ConfigHelper 缓存 → 刷新日志级别 —— 保存即生效，免重启。
    ///
    /// 【校验原则】任何一项不合法都整批拒绝保存（返回具体原因，窗体定位到违规项），
    ///   绝不"合法的先存、非法的跳过" —— 避免用户以为全存上了。
    /// </summary>
    public static class SystemConfigBLL
    {
        /// <summary>校验结果：Success=true 时 Items 为可保存清单；false 时 Message 为首个违规原因</summary>
        public class ValidateResult
        {
            public bool Success { get; set; } = false;
            public string Message { get; set; } = string.Empty;
            /// <summary>校验通过后的完整参数清单（保存用）</summary>
            public List<SystemConfigItem> Items = new List<SystemConfigItem>();
            /// <summary>违规参数的键名（窗体定位焦点用）</summary>
            public string InvalidKey { get; set; } = string.Empty;
        }

        // ============================================================
        // 1. 加载
        // ============================================================

        /// <summary>
        /// 加载全部参数（数据库有值用数据库，缺键用内置默认补齐）
        /// 【说明】读库失败时退回纯内置默认清单，保证配置窗体在任何情况下都能打开。
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
                LogHelper.Instance.Error("读取系统参数失败（将显示内置默认值）", ex);
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
        // 2. 校验
        // ============================================================

        /// <summary>
        /// 校验参数值（从窗体提交的 键→原始文本 字典校验）
        /// 【逐项规则】见 ValidateInt 各调用处的区间；【交叉规则】HeartbeatTimeoutMs &lt; HeartbeatIntervalMs。
        /// </summary>
        /// <param name="inputValues">键 → 用户输入的原始文本</param>
        public static ValidateResult Validate(Dictionary<string, string> inputValues)
        {
            ValidateResult result = new ValidateResult();

            if (inputValues == null)
            {
                result.Message = "参数数据无效";
                return result;
            }

            // ---------- 逐项范围校验 ----------
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

            // ---------- 枚举/布尔类校验 ----------
            string minLevel = GetString(inputValues, ConfigHelper.KEY_MIN_LOG_LEVEL, "INFO").ToUpperInvariant();
            if (minLevel != "DEBUG" && minLevel != "INFO" && minLevel != "WARN" && minLevel != "ERROR" && minLevel != "FATAL")
            {
                result.Message = "最低日志级别必须是 DEBUG / INFO / WARN / ERROR / FATAL 之一";
                result.InvalidKey = ConfigHelper.KEY_MIN_LOG_LEVEL;
                return result;
            }

            // ---------- 交叉校验：心跳超时必须小于心跳周期 ----------
            if (heartbeatTimeout.Value >= heartbeatInterval.Value)
            {
                result.Message = "交叉校验失败：心跳应答超时（" + heartbeatTimeout.Value
                                 + "ms）必须小于心跳周期（" + heartbeatInterval.Value
                                 + "ms），否则心跳永远来不及等到应答就被判超时";
                result.InvalidKey = ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS;
                return result;
            }

            // ---------- 全部通过：组装保存清单 ----------
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
            // [2026-09-11] 万总指令：700 预复位弃用 —— 兜底默认从 true 改 false；且窗体提交恒传 "false"，
            //   因此这里实际上永远落 "false"，兜底仅防极端情况下 inputValues 缺 key。
            AddItem(items, inputValues, ConfigHelper.KEY_TCP_RESET_ENABLED, GetBool(inputValues, ConfigHelper.KEY_TCP_RESET_ENABLED, false) ? "true" : "false");

            result.Success = true;
            result.Items = items;
            return result;
        }

        // ============================================================
        // 3. 保存
        // ============================================================

        /// <summary>
        /// 保存参数（先校验 → 单事务写库 → 刷新 ConfigHelper 缓存 → 刷新日志级别）
        /// 【保存即生效】心跳/超时等参数下一个使用周期生效；MinLogLevel 复位 LogHelper 缓存立即生效。
        /// </summary>
        public static ValidateResult Save(Dictionary<string, string> inputValues)
        {
            // 1. 先校验
            ValidateResult validate = Validate(inputValues);
            if (!validate.Success)
            {
                return validate;
            }

            // 2. 单事务写库
            try
            {
                SystemConfigDAL.SaveAll(validate.Items);
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("保存系统参数失败", ex);
                validate.Success = false;
                validate.Message = "保存失败：" + ex.Message;
                return validate;
            }

            // 3. 刷新内存缓存（保存即生效的关键一步）
            foreach (SystemConfigItem item in validate.Items)
            {
                ConfigHelper.UpdateCache(item.ConfigKey, item.ConfigValue);
            }

            // 4. 日志级别改动立即生效（复位 LogHelper 的解析缓存）
            LogHelper.Instance.RefreshMinLevel();

            LogHelper.Instance.Info("系统参数保存成功，共 " + validate.Items.Count + " 项，已即时生效");
            return validate;
        }

        /// <summary>
        /// 恢复全部默认值（配置窗体「恢复默认值」按钮：写库 + 刷缓存 + 刷日志级别）
        /// </summary>
        /// <returns>成功返回 true；失败 Message 里带原因</returns>
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
                LogHelper.Instance.Info("系统参数已全部恢复默认值（" + items.Count + " 项）");
                return true;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("恢复默认参数失败", ex);
                message = ex.Message;
                return false;
            }
        }

        // ============================================================
        // 私有校验辅助
        // ============================================================

        private class ValidateIntResult
        {
            public bool Ok = false;
            public int Value = 0;
            public string Key = string.Empty;
            public string Message = string.Empty;
        }

        /// <summary>整数校验：必须能转成整数且落在 [min, max] 区间</summary>
        private static ValidateIntResult ValidateInt(Dictionary<string, string> inputValues, string key, int min, int max)
        {
            ValidateIntResult result = new ValidateIntResult();
            result.Key = key;

            string raw = string.Empty;
            if (!inputValues.TryGetValue(key, out raw) || string.IsNullOrWhiteSpace(raw))
            {
                result.Message = "参数 " + key + " 不能为空";
                return result;
            }

            int value = 0;
            if (!int.TryParse(raw.Trim(), out value))
            {
                result.Message = "参数 " + key + " 必须是整数，当前输入：" + raw.Trim();
                return result;
            }

            if (value < min || value > max)
            {
                result.Message = "参数 " + key + " 必须在 " + min.ToString() + " ~ " + max.ToString() + " 之间，当前输入：" + value.ToString();
                return result;
            }

            result.Ok = true;
            result.Value = value;
            return result;
        }

        /// <summary>取字符串输入（无输入回落默认）</summary>
        private static string GetString(Dictionary<string, string> inputValues, string key, string defaultValue)
        {
            string raw = string.Empty;
            if (inputValues.TryGetValue(key, out raw) && !string.IsNullOrWhiteSpace(raw))
            {
                return raw.Trim();
            }
            return defaultValue;
        }

        /// <summary>取布尔输入（true/false/1/0/yes/no，其余回落默认）</summary>
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

        /// <summary>把校验后的键值加入保存清单（输入缺失时用当前值/默认值占位，保证清单完整）</summary>
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

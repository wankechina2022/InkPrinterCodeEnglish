namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 配置帮助类（v2 —— 配置文件彻底移除版）
    ///
    /// 【背景】v1 从 App.config 的 appSettings 读取参数。万总此前项目出现过
    ///   "反写配置文件损坏 XML 格式导致程序永远无法启动"的事故，2026-09-10 万总拍板：
    ///   彻底不用配置文件。参数改存数据库 SystemConfig 表（SQLite 事务写入原子，
    ///   不存在写一半），App.config 及其 NuGet 包（System.Configuration.ConfigurationManager）一并移除。
    ///
    /// 【配置来源只剩两层】
    ///   1. 内置默认值（本类常量 / GetDefaultValues，最底层兜底）；
    ///   2. SystemConfig 表（数据库，唯一可调源，由系统参数配置窗体维护）。
    ///
    /// 【机制】
    ///   - 程序启动时 SystemConfigDAL 把全表键值通过 LoadCache 灌进本类缓存；
    ///   - 配置窗体保存成功后由 SystemConfigBLL 调 UpdateCache 刷新 —— 保存即生效，免重启；
    ///   - 属性读取 = 缓存有值用缓存（附带区间合法性校验），无值/越界回落内置默认；
    ///   - 缓存加锁：配置窗体保存（UI 线程）与后台线程读参数可能并发。
    ///
    /// 【分层说明】本类在 Common 层，不引用 DAL/BLL；由 DAL 层主动"灌数据"进来
    ///   （DAL 引用 Common 合法，Common 反向引用 DAL 会破坏单向依赖）。
    ///
    /// 【边界】本类不写日志 —— LogHelper 自身要读本类的配置，反向调用会形成初始化循环依赖；
    ///   出异常只静默兜底（Debug.WriteLine）。
    /// </summary>
    public static class ConfigHelper
    {
        // ============================================================
        // 内置常量（不进数据库、不进配置页面的技术型参数）
        // ============================================================

        /// <summary>数据库所在文件夹名（相对程序目录）</summary>
        private const string DB_FOLDER_NAME = "Data";

        /// <summary>数据库文件名</summary>
        private const string DB_FILE_NAME = "InkPrinterCode.db";

        /// <summary>批量插入每批条数（内部调优参数，写死代码）</summary>
        public const int IMPORT_BATCH_SIZE = 1000;

        /// <summary>进度回报步长，每处理多少条回报一次（内部调优参数，写死代码）</summary>
        public const int PROGRESS_REPORT_STEP = 1000;

        /// <summary>
        /// SQL IN 查询 / 按主键批量删除的每批参数个数
        /// 【边界】SQLite 单条语句参数上限 999，取 500 留足余量（写死代码）。
        /// </summary>
        public const int SQL_IN_PARAM_BATCH_SIZE = 500;

        // ============================================================
        // 参数键名常量（SystemConfig 表的 ConfigKey，集中定义避免散落字符串）
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
        // 缓存（SystemConfig 表的内存镜像，键=ConfigKey，值=ConfigValue 原始字符串）
        // ============================================================

        private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// 把 SystemConfig 表全量键值灌入缓存（程序启动时由 SystemConfigDAL 调用一次）
        /// 【说明】传入 null 或空字典不视为错误 —— 此时所有参数走内置默认值。
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
        /// 单项刷新缓存（配置窗体保存成功后由 SystemConfigBLL 调用，保存即生效）
        /// </summary>
        public static void UpdateCache(string key, string value)
        {
            lock (_cacheLock)
            {
                _cache[key] = value;
            }
        }

        /// <summary>
        /// 取内置默认值清单（14 项可调参数）—— SystemConfigDAL 首次建库播种用
        /// 【说明】只返回 键→默认值；中文说明由 SystemConfigDAL 维护（保持 Common 层不引用 Model）。
        /// </summary>
        public static Dictionary<string, string> GetDefaultValues()
        {
            Dictionary<string, string> defaults = new Dictionary<string, string>();

            // ---------- 数据与日志组 ----------
            defaults[KEY_PAGE_SIZE] = "100";
            defaults[KEY_LOG_KEEP_DAYS] = "30";
            defaults[KEY_DUPLICATE_LOG_LIMIT] = "500";
            defaults[KEY_RUN_LOG_MAX_LINES] = "100";
            defaults[KEY_ENABLE_LOGGING] = "true";
            defaults[KEY_MIN_LOG_LEVEL] = "INFO";
            defaults[KEY_DEDUP_HASHSET_THRESHOLD] = "1000000";

            // ---------- 喷码通讯组 ----------
            defaults[KEY_HEARTBEAT_INTERVAL_MS] = "3000";
            defaults[KEY_SEND_RESPONSE_TIMEOUT_MS] = "3000";
            defaults[KEY_HEARTBEAT_TIMEOUT_MS] = "2000";
            defaults[KEY_RECONNECT_INTERVAL_MS] = "5000";
            defaults[KEY_INITIAL_CACHE_COUNT] = "3";
            defaults[KEY_MAX_RETRY_COUNT] = "3";
            // [2026-09-11] 万总指令：700 端口预复位功能整体弃用 —— 默认值改为 false（不勾选），
            //   UI 复选框已隐藏、保存恒写 false、恢复默认值也回 false（本字典同时供恢复默认按钮使用），四条路全收敛。
            defaults[KEY_TCP_RESET_ENABLED] = "false";

            return defaults;
        }

        // ============================================================
        // 数据库相关（固定常量，不可调）
        // ============================================================

        /// <summary>数据库所在文件夹的完整路径（程序目录 + Data）</summary>
        public static string DbFolderPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DB_FOLDER_NAME); }
        }

        /// <summary>数据库文件完整路径</summary>
        public static string DbFilePath
        {
            get { return Path.Combine(DbFolderPath, DB_FILE_NAME); }
        }

        /// <summary>
        /// SQLite 连接字符串
        /// 【说明】Microsoft.Data.Sqlite 的连接串很简单，Data Source 指向 db 文件即可；
        ///   文件不存在时会自动创建（目录必须先存在，由 DbInitializer 负责创建目录）。
        /// </summary>
        public static string ConnectionString
        {
            get { return "Data Source=" + DbFilePath + ";"; }
        }

        // ============================================================
        // 界面相关（SystemConfig 表可调）
        // ============================================================

        /// <summary>数据查看页每页条数，默认 100，允许 10~1000</summary>
        public static int PageSize
        {
            get { return GetInt(KEY_PAGE_SIZE, 100, 10, 1000); }
        }

        // ============================================================
        // 日志相关（SystemConfig 表可调）
        // ============================================================

        /// <summary>日志总开关，默认开启</summary>
        public static bool EnableLogging
        {
            get { return GetBool(KEY_ENABLE_LOGGING, true); }
        }

        /// <summary>
        /// 最低日志级别文本（DEBUG/INFO/WARN/ERROR/FATAL），默认 INFO
        /// 【说明】这里故意返回 string 而不是 LogHelper.LogLevel 枚举 —— 避免 ConfigHelper 与
        ///   LogHelper 互相引用类型造成耦合，解析动作放在 LogHelper 内部完成。
        /// </summary>
        public static string MinLogLevelText
        {
            get { return GetString(KEY_MIN_LOG_LEVEL, "INFO"); }
        }

        /// <summary>日志保留天数，默认 30；0 表示永不清理</summary>
        public static int LogKeepDays
        {
            get { return GetInt(KEY_LOG_KEEP_DAYS, 30, 0, 365); }
        }

        /// <summary>单次导入写入重复码明细日志的条数上限，默认 500</summary>
        public static int DuplicateLogLimit
        {
            get { return GetInt(KEY_DUPLICATE_LOG_LIMIT, 500, 100, 5000); }
        }

        /// <summary>主界面运行日志最大行数，默认 100（阶段二 txtRunLog 截尾用）</summary>
        public static int RunLogMaxLines
        {
            get { return GetInt(KEY_RUN_LOG_MAX_LINES, 100, 10, 1000); }
        }

        // ============================================================
        // 导入性能相关
        // ============================================================

        /// <summary>
        /// 去重双路径阈值，默认 100 万
        /// 【机制】库内 CodeData 总条数 &lt;= 该值时，一次性把库里所有码值读进内存 HashSet 做去重（最快）；
        ///   超过该值则改用分批 IN 查询，牺牲一点速度换内存安全。
        /// </summary>
        public static int DedupHashSetThreshold
        {
            get { return GetInt(KEY_DEDUP_HASHSET_THRESHOLD, 1000000, 10000, 100000000); }
        }

        /// <summary>批量插入每批条数（内置常量 1000，写死代码）</summary>
        public static int ImportBatchSize
        {
            get { return IMPORT_BATCH_SIZE; }
        }

        /// <summary>进度回报步长（内置常量 1000，写死代码）</summary>
        public static int ProgressReportStep
        {
            get { return PROGRESS_REPORT_STEP; }
        }

        /// <summary>SQL IN 查询每批参数个数（内置常量 500，写死代码）</summary>
        public static int SqlInParamBatchSize
        {
            get { return SQL_IN_PARAM_BATCH_SIZE; }
        }

        // ============================================================
        // 喷码通讯相关（SystemConfig 表可调，阶段二新增）
        // ============================================================

        /// <summary>
        /// 心跳周期毫秒，默认 3000，允许 500~600000
        /// [2026-09-10] 万总裁定：心跳不设开关、必须常开 —— 本项目心跳（查询状态指令）
        ///   既是探活也是唯一的断线判定手段，关掉即无法感知断线。规约"心跳机制带开关"条目
        ///   在本项目有意省略，属已备案的规约差异（见阶段二审计报告）。
        /// </summary>
        public static int HeartbeatIntervalMs
        {
            get { return GetInt(KEY_HEARTBEAT_INTERVAL_MS, 3000, 500, 600000); }
        }

        /// <summary>
        /// 指令应答超时毫秒，默认 3000，允许 500~60000。
        /// 【适用】仅"打印信号设置 / 清三条队列（启动与重连）"这类控制指令 —— 必须给足应答时间。
        /// [2026-09-10] 【不适用】生产发码（SendOneCode）已改用 PrintServiceBLL.SEND_ACK_TIMEOUT_MS（100ms 常量），
        ///   目的是压缩业务锁持锁时长、避免长事务堵住心跳导致误判断线；本配置项不再参与发码路径。
        /// </summary>
        public static int SendResponseTimeoutMs
        {
            get { return GetInt(KEY_SEND_RESPONSE_TIMEOUT_MS, 3000, 500, 60000); }
        }

        /// <summary>心跳应答超时毫秒，默认 2000，允许 200~30000</summary>
        public static int HeartbeatTimeoutMs
        {
            get { return GetInt(KEY_HEARTBEAT_TIMEOUT_MS, 2000, 200, 30000); }
        }

        /// <summary>断线自动重连间隔毫秒，默认 5000，允许 1000~600000</summary>
        public static int ReconnectIntervalMs
        {
            get { return GetInt(KEY_RECONNECT_INTERVAL_MS, 5000, 1000, 600000); }
        }

        /// <summary>喷码机预填缓存条数，默认 3，允许 1~20</summary>
        public static int InitialCacheCount
        {
            get { return GetInt(KEY_INITIAL_CACHE_COUNT, 3, 1, 20); }
        }

        /// <summary>
        /// 单条码发送失败重试上限，默认 3，允许 0~10。
        /// [2026-09-10] 已停用（保留配置项与表记录，勿删）：取码占用制下"一个码只写一次、
        ///   绝不重试"已封版（万总 17:29 指令），重试等同于对同一条码二次写入喷码机。
        ///   本项当前不参与任何业务逻辑，仅供将来若恢复重试机制时复用。
        /// </summary>
        public static int MaxRetryCount
        {
            get { return GetInt(KEY_MAX_RETRY_COUNT, 3, 0, 10); }
        }

        /// <summary>TCP 连接前是否向 700 端口预复位。
        /// [2026-09-11] 万总指令：功能弃用，默认 false（不勾选）—— 种子默认、保存提交、恢复默认三路也全改为 false。</summary>
        public static bool TcpResetEnabled
        {
            get { return GetBool(KEY_TCP_RESET_ENABLED, false); }
        }

        // ============================================================
        // 其他
        // ============================================================

        /// <summary>
        /// 操作人名称
        /// 【兜底】取当前 Windows 登录用户名；取不到时返回 "unknown"。
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
                    System.Diagnostics.Debug.WriteLine("ConfigHelper 读取当前用户名失败：" + ex.Message);
                    return "unknown";
                }
            }
        }

        // ============================================================
        // 私有取值方法（统一兜底入口：缓存 → 区间校验 → 内置默认）
        // ============================================================

        /// <summary>从缓存取原始字符串（加锁，防配置窗体保存与后台读取并发）</summary>
        private static string? GetRawValue(string key)
        {
            lock (_cacheLock)
            {
                string? value = null;
                _cache.TryGetValue(key, out value);
                return value;
            }
        }

        /// <summary>读取字符串配置：缓存无值/为空时回落默认值</summary>
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
                System.Diagnostics.Debug.WriteLine("ConfigHelper 读取配置[" + key + "]失败：" + ex.Message);
                return defaultValue;
            }
        }

        /// <summary>读取整数配置，并限制在 [minValue, maxValue] 区间内（越界回落默认）</summary>
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
                System.Diagnostics.Debug.WriteLine("ConfigHelper 读取配置[" + key + "]失败：" + ex.Message);
                return defaultValue;
            }
        }

        /// <summary>读取布尔配置。支持 true/false，也兼容 1/0、yes/no 写法</summary>
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
                System.Diagnostics.Debug.WriteLine("ConfigHelper 读取配置[" + key + "]失败：" + ex.Message);
                return defaultValue;
            }
        }
    }
}

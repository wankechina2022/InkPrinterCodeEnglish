using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] 系统参数配置数据访问类（SystemConfig 表，键值对）
    ///
    /// 【职责】播种内置默认值、全量读取（供 ConfigHelper 灌缓存）、全量保存（配置窗体）。
    ///
    /// 【三条红线】
    ///   1. 只用 CREATE TABLE IF NOT EXISTS（建表在 DbInitializer），本类绝不改表结构；
    ///   2. 全参数化，禁止字符串拼值；
    ///   3. 全量保存在单事务内提交，要么全部成功要么全部不变，绝不出现半批新半批旧。
    ///
    /// 【分层】DAL 引用 Common（ConfigHelper 取默认值与键名）+ Model（实体），方向合法。
    /// </summary>
    public static class SystemConfigDAL
    {
        /// <summary>各项参数的中文说明（播种时写入 Description 列，给后台查库排查用）</summary>
        private static readonly Dictionary<string, string> _descriptions = BuildDescriptions();

        // ============================================================
        // 1. 播种
        // ============================================================

        /// <summary>
        /// 播种内置默认参数（INSERT OR IGNORE：已存在的键绝不覆盖 —— 保护用户已改过的值）
        /// </summary>
        /// <returns>本次实际新插入的条数（0 = 全部已存在）</returns>
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
        // 2. 读取
        // ============================================================

        /// <summary>
        /// 全量读取参数（键 → 值），供 ConfigHelper.LoadCache 灌缓存用
        /// 【说明】不用 SELECT *（红线），只取需要的两列。
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
        // 3. 保存（单事务全量覆盖）
        // ============================================================

        /// <summary>
        /// 全量保存参数清单（单事务，全部成功或全部不变）
        /// 【说明】用 INSERT OR REPLACE（SQLite 的 upsert 写法）——
        ///   键存在则整行替换（ConfigValue / Description / UpdateTime 一起更新），不存在则插入。
        /// </summary>
        /// <param name="items">要保存的参数清单（来自配置窗体，已经过 BLL 校验）</param>
        /// <returns>成功保存的条数（失败会整体回滚并抛出异常，由 BLL 捕获）</returns>
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
        // 私有辅助
        // ============================================================

        /// <summary>取参数中文说明（说明清单里没有的键返回空串）</summary>
        private static string GetDescription(string key)
        {
            string description = string.Empty;
            _descriptions.TryGetValue(key, out description);
            return description;
        }

        /// <summary>构建说明清单（键名与 ConfigHelper.KEY_* 一一对应）</summary>
        private static Dictionary<string, string> BuildDescriptions()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();

            map[ConfigHelper.KEY_PAGE_SIZE] = "数据查看页每页条数（10~1000）";
            map[ConfigHelper.KEY_LOG_KEEP_DAYS] = "日志保留天数，0=永不清理（0~365）";
            map[ConfigHelper.KEY_DUPLICATE_LOG_LIMIT] = "单次导入重复码明细日志条数上限（100~5000）";
            map[ConfigHelper.KEY_RUN_LOG_MAX_LINES] = "主界面运行日志最大行数，超出自动截尾（10~1000）";
            map[ConfigHelper.KEY_ENABLE_LOGGING] = "日志总开关（false=只保留 error 副本）";
            map[ConfigHelper.KEY_MIN_LOG_LEVEL] = "最低日志级别（DEBUG/INFO/WARN/ERROR/FATAL）";
            map[ConfigHelper.KEY_DEDUP_HASHSET_THRESHOLD] = "去重内存 HashSet 阈值，超过改走分批 IN 查询（10000~100000000）";
            map[ConfigHelper.KEY_HEARTBEAT_INTERVAL_MS] = "心跳周期毫秒（500~600000），必须大于心跳超时";
            map[ConfigHelper.KEY_SEND_RESPONSE_TIMEOUT_MS] = "发指令/数据等 0x06 应答超时毫秒（500~60000）";
            map[ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS] = "心跳应答超时毫秒（200~30000），必须小于心跳周期";
            map[ConfigHelper.KEY_RECONNECT_INTERVAL_MS] = "断线自动重连间隔毫秒（1000~600000）";
            map[ConfigHelper.KEY_INITIAL_CACHE_COUNT] = "喷码机预填缓存条数（1~20）";
            map[ConfigHelper.KEY_MAX_RETRY_COUNT] = "单条码发送失败重试上限（0~10）";
            map[ConfigHelper.KEY_TCP_RESET_ENABLED] = "TCP 连接前向 700 端口预复位（现场经验，可关闭）";

            return map;
        }
    }
}

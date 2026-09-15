using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] 数据库初始化类 —— 建库、建表、建索引
    ///
    /// 【核心约束（开发规约红线）】
    ///   1. 只使用 CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT EXISTS；
    ///   2. 绝不出现 ALTER TABLE、DROP TABLE、DROP INDEX —— 已存在的表结构一律不动，
    ///      结构变更必须由人工确认后执行，程序不得自动改表（避免现场数据被程序悄悄改坏）。
    ///   3. 首次运行建表、之后每次启动都走"存在即跳过"的分支，重复启动不会产生任何副作用。
    ///
    /// 【调用时机】Program.Main 里，在创建主窗体之前调用一次。
    ///   初始化失败时由调用方决定是提示后退出，还是带警告继续（当前策略：记 FATAL 日志 + 提示后退出）。
    ///
    /// 【为什么开 WAL】
    ///   journal_mode=WAL 让"读"和"写"不再互相阻塞 —— 阶段二会出现"后台线程写喷印结果、
    ///   界面线程刷看板"的并发场景，不开 WAL 很容易撞上 database is locked。
    /// </summary>
    public static class DbInitializer
    {
        // ============================================================
        // DDL 语句（全部带 IF NOT EXISTS）
        // ============================================================

        /// <summary>导入台账表</summary>
        private const string SQL_CREATE_IMPORT_BATCH = @"
CREATE TABLE IF NOT EXISTS ImportBatch (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    BatchNo        TEXT    NOT NULL DEFAULT '',
    SourceType     INTEGER NOT NULL DEFAULT 0,
    FileName       TEXT    NOT NULL DEFAULT '',
    FilePath       TEXT    NOT NULL DEFAULT '',
    TotalRows      INTEGER NOT NULL DEFAULT 0,
    ValidCount     INTEGER NOT NULL DEFAULT 0,
    DuplicateCount INTEGER NOT NULL DEFAULT 0,
    InvalidCount   INTEGER NOT NULL DEFAULT 0,
    ImportTime     TEXT    NOT NULL DEFAULT '',
    Operator       TEXT    NOT NULL DEFAULT '',
    Remark         TEXT    NOT NULL DEFAULT ''
);";

        /// <summary>码数据表</summary>
        private const string SQL_CREATE_CODE_DATA = @"
CREATE TABLE IF NOT EXISTS CodeData (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    BatchId      INTEGER NOT NULL DEFAULT 0,
    RowNo        INTEGER NOT NULL DEFAULT 0,
    CodeValue    TEXT    NOT NULL DEFAULT '',
    PrintStatus  INTEGER NOT NULL DEFAULT 0,
    SendTime     TEXT    NOT NULL DEFAULT '',
    PrintTime    TEXT    NOT NULL DEFAULT '',
    FeedbackText TEXT    NOT NULL DEFAULT '',
    RetryCount   INTEGER NOT NULL DEFAULT 0,
    CreateTime   TEXT    NOT NULL DEFAULT ''
);";

        /// <summary>批次号唯一索引 —— 保证台账批次号不重复</summary>
        private const string SQL_INDEX_BATCH_NO = @"
CREATE UNIQUE INDEX IF NOT EXISTS UX_ImportBatch_BatchNo ON ImportBatch(BatchNo);";

        /// <summary>
        /// 码值唯一索引 —— 全局去重的根基
        /// 【说明】重复判定是"全局唯一"：跨文件、跨批次同一个码都算重复。
        ///   靠这个唯一索引兜底，即使上层逻辑漏判，数据库也会直接拒绝插入。
        /// </summary>
        private const string SQL_INDEX_CODE_VALUE = @"
CREATE UNIQUE INDEX IF NOT EXISTS UX_CodeData_CodeValue ON CodeData(CodeValue);";

        /// <summary>
        /// 状态 + 主键复合索引 —— 阶段二"取下一个待喷码"走 WHERE PrintStatus=0 ORDER BY Id
        /// </summary>
        private const string SQL_INDEX_STATUS_ID = @"
CREATE INDEX IF NOT EXISTS IX_CodeData_Status_Id ON CodeData(PrintStatus, Id);";

        /// <summary>入库时间索引 —— 数据查看页的时间范围筛选</summary>
        private const string SQL_INDEX_CREATE_TIME = @"
CREATE INDEX IF NOT EXISTS IX_CodeData_CreateTime ON CodeData(CreateTime);";

        /// <summary>批次外键索引 —— 按批次追溯、按批次统计</summary>
        private const string SQL_INDEX_BATCH_ID = @"
CREATE INDEX IF NOT EXISTS IX_CodeData_BatchId ON CodeData(BatchId);";

        /// <summary>
        /// 喷码机连接配置表（[2026-09-10] 阶段二新增，双行设计：TCP 一行、串口一行）
        /// 【切换机制】IsEnabled 标志决定启用哪一行，切换只改标志、参数互不覆盖（万总要求）。
        /// </summary>
        private const string SQL_CREATE_PRINTER_CONFIG = @"
CREATE TABLE IF NOT EXISTS PrinterConfig (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ConnType        INTEGER NOT NULL DEFAULT 0,
    IsEnabled       INTEGER NOT NULL DEFAULT 0,
    TcpIp           TEXT    NOT NULL DEFAULT '',
    TcpPort         INTEGER NOT NULL DEFAULT 7000,
    ResetPort       INTEGER NOT NULL DEFAULT 700,
    SerialPortName  TEXT    NOT NULL DEFAULT '',
    SerialBaudRate  INTEGER NOT NULL DEFAULT 9600,
    UpdateTime      TEXT    NOT NULL DEFAULT ''
);";

        /// <summary>连接类型索引 —— 按类型取配置行</summary>
        private const string SQL_INDEX_PRINTER_CONFIG_TYPE = @"
CREATE INDEX IF NOT EXISTS IX_PrinterConfig_ConnType ON PrinterConfig(ConnType);";

        /// <summary>
        /// 系统参数配置表（[2026-09-10] 阶段二新增，键值对）
        /// 【背景】App.config 配置文件已废弃（万总拍板：反写配置文件有格式损坏导致
        ///   程序无法启动的历史事故），全部可调参数迁入本表，首次建库播种内置默认值。
        /// </summary>
        private const string SQL_CREATE_SYSTEM_CONFIG = @"
CREATE TABLE IF NOT EXISTS SystemConfig (
    ConfigKey    TEXT PRIMARY KEY,
    ConfigValue  TEXT NOT NULL DEFAULT '',
    Description  TEXT NOT NULL DEFAULT '',
    UpdateTime   TEXT NOT NULL DEFAULT ''
);";

        // ============================================================
        // 初始化入口
        // ============================================================

        /// <summary>
        /// 执行数据库初始化
        /// </summary>
        /// <returns>成功返回 true；目录不可用、建表失败返回 false（详细原因看 error 日志）</returns>
        public static bool Initialize()
        {
            try
            {
                if (!ValidateHelper.EnsureFolderExists(ConfigHelper.DbFolderPath))
                {
                    LogHelper.Instance.Fatal("数据库目录不可用，初始化失败：" + ConfigHelper.DbFolderPath);
                    return false;
                }

                bool dbFileExists = File.Exists(ConfigHelper.DbFilePath);

                EnableWalMode();

                SqliteHelper.ExecuteNonQuery(SQL_CREATE_IMPORT_BATCH);
                SqliteHelper.ExecuteNonQuery(SQL_CREATE_CODE_DATA);
                SqliteHelper.ExecuteNonQuery(SQL_CREATE_PRINTER_CONFIG);
                SqliteHelper.ExecuteNonQuery(SQL_CREATE_SYSTEM_CONFIG);

                SqliteHelper.ExecuteNonQuery(SQL_INDEX_BATCH_NO);
                SqliteHelper.ExecuteNonQuery(SQL_INDEX_CODE_VALUE);
                SqliteHelper.ExecuteNonQuery(SQL_INDEX_STATUS_ID);
                SqliteHelper.ExecuteNonQuery(SQL_INDEX_CREATE_TIME);
                SqliteHelper.ExecuteNonQuery(SQL_INDEX_BATCH_ID);
                SqliteHelper.ExecuteNonQuery(SQL_INDEX_PRINTER_CONFIG_TYPE);

                // ---------- 播种与参数加载（[2026-09-10] 阶段二新增） ----------
                //   播种：首次建库把内置默认参数 INSERT OR IGNORE 进 SystemConfig（已有值不覆盖）；
                //   加载：把 SystemConfig 全表键值灌进 ConfigHelper 内存缓存，此后读参数全走缓存；
                //   台账：PrinterConfig 确保存在 TCP/串口两行。
                //   【边界】播种/加载失败不阻止程序启动 —— 参数全部回落内置默认值，只记 WARN。
                SeedDefaultData();

                if (dbFileExists)
                {
                    LogHelper.Instance.Info("数据库初始化完成（库文件已存在，表结构未做任何变更）：" + ConfigHelper.DbFilePath);
                }
                else
                {
                    LogHelper.Instance.Info("数据库初始化完成（首次创建库文件与数据表）：" + ConfigHelper.DbFilePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Fatal("数据库初始化失败：" + ConfigHelper.DbFilePath, ex);
                return false;
            }
        }

        // ============================================================
        // 私有辅助
        // ============================================================

        /// <summary>
        /// 播种默认参数 + 加载参数缓存 + 确保 PrinterConfig 两行存在（[2026-09-10] 新增）
        /// 【调用时机】建表完成后、主窗体创建前，每次启动都执行（各步骤自身幂等）。
        /// 【边界】任何一步失败都只记 WARN 并继续 —— 参数回落内置默认值，程序照常启动，
        ///   绝不允许"参数播种失败导致程序起不来"这种本末倒置的情况。
        /// </summary>
        private static void SeedDefaultData()
        {
            // 1. 播种内置默认参数（INSERT OR IGNORE：表里已有的值绝不覆盖）
            try
            {
                int inserted = SystemConfigDAL.SeedDefaults();
                if (inserted > 0)
                {
                    LogHelper.Instance.Info("SystemConfig 参数播种完成，新增 " + inserted + " 项默认参数");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("SystemConfig 参数播种失败（参数将全部使用内置默认值）：" + ex.Message);
            }

            // 2. 加载参数缓存（配置读取从这一刻起走数据库值 + 内置默认兜底）
            try
            {
                Dictionary<string, string> values = SystemConfigDAL.LoadAll();
                ConfigHelper.LoadCache(values);
                LogHelper.Instance.Info("SystemConfig 参数缓存加载完成，共 " + values.Count + " 项");
            }
            catch (Exception ex)
            {
                ConfigHelper.LoadCache(null);
                LogHelper.Instance.Warn("SystemConfig 参数缓存加载失败（参数将全部使用内置默认值）：" + ex.Message);
            }

            // 3. 确保 PrinterConfig 表有 TCP / 串口两行配置
            try
            {
                PrinterConfigDAL.EnsureRows();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("PrinterConfig 配置行检查失败（打开喷码机配置窗体时会重试）：" + ex.Message);
            }
        }

        /// <summary>
        /// 开启 WAL 日志模式
        /// 【边界】个别环境（如某些网络盘、加密盘）不支持 WAL，此时不视为致命错误 ——
        ///   WAL 只是性能优化，开不了就退回默认的 delete 模式，功能不受影响，因此只记 WARN。
        /// </summary>
        private static void EnableWalMode()
        {
            try
            {
                object? result = SqliteHelper.ExecuteScalar("PRAGMA journal_mode=WAL;");
                string mode = string.Empty;
                if (result != null)
                {
                    mode = result.ToString() ?? string.Empty;
                }

                if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    LogHelper.Instance.Info("SQLite 已开启 WAL 模式");
                }
                else
                {
                    LogHelper.Instance.Warn("SQLite 未能开启 WAL 模式，当前模式：" + mode + "（不影响功能，仅并发读写性能略降）");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("设置 SQLite WAL 模式失败（不影响功能）：" + ex.Message);
            }
        }
    }
}

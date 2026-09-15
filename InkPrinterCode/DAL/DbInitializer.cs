using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] Database initialisation class -- creates the database, tables and indexes
    ///
    /// [Core constraints (non-negotiable development rules)]
    ///   1. Only CREATE TABLE IF NOT EXISTS / CREATE INDEX IF NOT EXISTS are used;
    ///   2. ALTER TABLE, DROP TABLE and DROP INDEX never appear -- existing table structures are never touched,
    ///      structural changes must be confirmed and applied manually; the program must not alter tables automatically (so live data is never quietly corrupted).
    ///   3. Tables are created on first run and every later start takes the "already exists, skip" branch, so repeated starts have no side effects.
    ///
    /// [When to call] Once from Program.Main, before the main form is created.
    ///   On initialisation failure the caller decides whether to exit after notifying the user or continue with a warning (current policy: log FATAL + notify then exit).
    ///
    /// [Why WAL is enabled]
    ///   journal_mode=WAL stops reads and writes from blocking each other -- stage two introduces the concurrent scenario of a background thread
    ///   writing print results while the UI thread refreshes the dashboard, and without WAL it easily hits "database is locked".
    /// </summary>
    public static class DbInitializer
    {
        // ============================================================
        // DDL statements (all with IF NOT EXISTS)
        // ============================================================

        /// <summary>Import ledger table</summary>
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

        /// <summary>Code data table</summary>
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

        /// <summary>Unique index on the batch number -- guarantees ledger batch numbers are unique</summary>
        private const string SQL_INDEX_BATCH_NO = @"
CREATE UNIQUE INDEX IF NOT EXISTS UX_ImportBatch_BatchNo ON ImportBatch(BatchNo);";

        /// <summary>
        /// Code-value unique index —— the foundation of global deduplication
        /// [Notes] Duplicate determination is "globally unique": the same code counts as a duplicate across files and
        ///   across batches. This unique index is the backstop, so even if the upper-layer logic misses a case the
        ///   database rejects the insert outright.
        /// </summary>
        private const string SQL_INDEX_CODE_VALUE = @"
CREATE UNIQUE INDEX IF NOT EXISTS UX_CodeData_CodeValue ON CodeData(CodeValue);";

        /// <summary>
        /// Composite status + primary key index -- stage two's "take the next code to print" runs WHERE PrintStatus=0 ORDER BY Id
        /// </summary>
        private const string SQL_INDEX_STATUS_ID = @"
CREATE INDEX IF NOT EXISTS IX_CodeData_Status_Id ON CodeData(PrintStatus, Id);";

        /// <summary>Insert-time index -- supports the time-range filter on the data view page</summary>
        private const string SQL_INDEX_CREATE_TIME = @"
CREATE INDEX IF NOT EXISTS IX_CodeData_CreateTime ON CodeData(CreateTime);";

        /// <summary>Batch foreign-key index —— tracing by batch and statistics by batch</summary>
        private const string SQL_INDEX_BATCH_ID = @"
CREATE INDEX IF NOT EXISTS IX_CodeData_BatchId ON CodeData(BatchId);";

        /// <summary>
        /// Inkjet printer connection config table ([2026-09-10] added in stage two; two-row design: one TCP row, one serial row)
        /// [Switch mechanism] The IsEnabled flag decides which row is active; switching only changes the flag and the parameters never overwrite each other (a design requirement).
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

        /// <summary>Connection type index -- fetch a config row by type</summary>
        private const string SQL_INDEX_PRINTER_CONFIG_TYPE = @"
CREATE INDEX IF NOT EXISTS IX_PrinterConfig_ConnType ON PrinterConfig(ConnType);";

        /// <summary>
        /// System parameter configuration table (added on [2026-09-10]; key-value pairs)
        /// [Background] The App.config configuration file has been abandoned (writing back to the
        ///   config file once caused a past incident where a corrupted config file prevented startup, and the program could not
        ///   start), so all adjustable parameters were migrated into this table, with the built-in defaults seeded when
        ///   the database is first created.
        /// </summary>
        private const string SQL_CREATE_SYSTEM_CONFIG = @"
CREATE TABLE IF NOT EXISTS SystemConfig (
    ConfigKey    TEXT PRIMARY KEY,
    ConfigValue  TEXT NOT NULL DEFAULT '',
    Description  TEXT NOT NULL DEFAULT '',
    UpdateTime   TEXT NOT NULL DEFAULT ''
);";

        // ============================================================
        // Initialization entry point
        // ============================================================

        /// <summary>
        /// Perform database initialization
        /// </summary>
        /// <returns>Returns true on success; returns false when the directory is unavailable or table creation fails (see the error log for the detailed reason)</returns>
        public static bool Initialize()
        {
            try
            {
                if (!ValidateHelper.EnsureFolderExists(ConfigHelper.DbFolderPath))
                {
                    LogHelper.Instance.Fatal("The database directory is unavailable; initialization failed: " + ConfigHelper.DbFolderPath);
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

                // ---------- Seeding and parameter loading (added on [2026-09-10]) ----------
                //   Seeding: on first database creation, INSERT OR IGNORE the built-in default parameters into
                //     SystemConfig (existing values are never overwritten);
                //   Loading: pour the whole SystemConfig table's key/value pairs into ConfigHelper's in-memory cache;
                //     from then on all parameter reads go through the cache;
                //   Ledger: PrinterConfig is ensured to have both the TCP and serial rows.
                //   [Boundaries] A seeding/loading failure does not prevent the program from starting —— all parameters
                //     fall back to the built-in defaults and only a WARN is logged.
                SeedDefaultData();

                if (dbFileExists)
                {
                    LogHelper.Instance.Info("Database initialization complete (the database file already existed, and no table structure was changed): " + ConfigHelper.DbFilePath);
                }
                else
                {
                    LogHelper.Instance.Info("Database initialization complete (database file and data tables created for the first time): " + ConfigHelper.DbFilePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Fatal("Database initialization failed: " + ConfigHelper.DbFilePath, ex);
                return false;
            }
        }

        // ============================================================
        // Private helpers
        // ============================================================

        /// <summary>
        /// Seed the default parameters + load the parameter cache + ensure the two PrinterConfig rows exist (added [2026-09-10])
        /// [Call timing] After table creation completes and before the main form is created; executed on every startup
        /// (each step is idempotent in itself).
        /// [Boundaries] A failure at any step only logs a WARN and continues —— the parameters fall back to the built-in
        ///   defaults and the program starts as usual; a situation as backwards as "parameter seeding failed so the
        ///   program cannot start" is never allowed.
        /// </summary>
        private static void SeedDefaultData()
        {
            // 1. Seed the built-in default parameters (INSERT OR IGNORE: values already in the table are never overwritten)
            try
            {
                int inserted = SystemConfigDAL.SeedDefaults();
                if (inserted > 0)
                {
                    LogHelper.Instance.Info("SystemConfig parameter seeding complete; added " + inserted + " default parameters");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("SystemConfig parameter seeding failed (all parameters will use the built-in defaults): " + ex.Message);
            }

            // 2. Load the parameter cache (from this moment on, configuration reads use the database values with the
            //    built-in defaults as the fallback)
            try
            {
                Dictionary<string, string> values = SystemConfigDAL.LoadAll();
                ConfigHelper.LoadCache(values);
                LogHelper.Instance.Info("SystemConfig parameter cache loaded, " + values.Count + " entries in total");
            }
            catch (Exception ex)
            {
                ConfigHelper.LoadCache(null);
                LogHelper.Instance.Warn("SystemConfig parameter cache loading failed (all parameters will use the built-in defaults): " + ex.Message);
            }

            // 3. Ensure the PrinterConfig table has both the TCP and serial configuration rows
            try
            {
                PrinterConfigDAL.EnsureRows();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("PrinterConfig configuration row check failed (it will be retried when the inkjet printer configuration form is opened): " + ex.Message);
            }
        }

        /// <summary>
        /// Enable WAL journal mode
        /// [Boundaries] Some environments (such as certain network drives and encrypted drives) do not support WAL, and
        ///   this is not treated as fatal —— WAL is only a performance optimization, so if it cannot be enabled it falls
        ///   back to the default delete mode with the functionality unaffected; hence only a WARN is logged.
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
                    LogHelper.Instance.Info("SQLite WAL mode enabled");
                }
                else
                {
                    LogHelper.Instance.Warn("SQLite failed to enable WAL mode; current mode: " + mode + " (functionality is unaffected; only concurrent read/write performance is slightly reduced)");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Warn("Failed to set SQLite WAL mode (does not affect functionality): " + ex.Message);
            }
        }
    }
}

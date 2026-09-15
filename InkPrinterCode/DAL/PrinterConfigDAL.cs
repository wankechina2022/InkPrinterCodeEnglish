using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.Model;
using InkPrinterCode.Model.Enums;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] Inkjet printer connection config data access class (PrinterConfig table, two-row TCP/serial design)
    ///
    /// [Two-row mechanism (choose one of two and switch back and forth; switching must not lose config content, only change the enabled flag)]
    ///   - The table always holds two rows: one with ConnType=0 (TCP) and one with ConnType=1 (serial), each storing its own parameters;
    ///   - The row with IsEnabled=1 is the currently enabled connection method, and at most one row is enabled;
    ///   - Saving = inside a single transaction: update each row's parameters separately (never overwriting each other) plus a mutually exclusive switch of the enabled flag.
    ///
    /// [Non-negotiable] Read-only with no table-structure changes; fully parameterised; the enable switch completes inside a single transaction (never leaving both rows
    ///   enabled or both disabled as an intermediate state).
    /// </summary>
    public static class PrinterConfigDAL
    {
        // ============================================================
        // 1. Row guarantee
        // ============================================================

        /// <summary>
        /// Ensure the table contains both the TCP and serial config rows (seeded on first start, idempotently skipped afterwards)
        /// [Notes] Called by DbInitializer at start-up; also called as a fallback before the config form opens if the rows are still missing.
        /// </summary>
        public static void EnsureRows()
        {
            int count = Extensions.ToInt(SqliteHelper.ExecuteScalar("SELECT COUNT(1) FROM PrinterConfig;"), 0);
            if (count >= 2)
            {
                return;
            }

            string sql = @"INSERT INTO PrinterConfig (ConnType, IsEnabled, TcpIp, TcpPort, ResetPort, SerialPortName, SerialBaudRate, UpdateTime)
                           SELECT @ConnType, @IsEnabled, @TcpIp, @TcpPort, @ResetPort, @SerialPortName, @SerialBaudRate, @UpdateTime
                           WHERE NOT EXISTS (SELECT 1 FROM PrinterConfig WHERE ConnType = @ConnType);";

            InsertRowIfMissing(sql, ConnType.Tcp, 1, "192.168.1.100", 7000, 700, string.Empty, 9600);
            InsertRowIfMissing(sql, ConnType.Serial, 0, string.Empty, 7000, 700, "COM1", 9600);
        }

        /// <summary>Insert one configuration row by connection type (skipped if the row already exists)</summary>
        private static void InsertRowIfMissing(string sql, ConnType connType, int isEnabled,
            string tcpIp, int tcpPort, int resetPort, string serialPortName, int serialBaudRate)
        {
            SqliteParameter[] parameters = new SqliteParameter[]
            {
                SqliteHelper.CreateParameter("@ConnType", (int)connType),
                SqliteHelper.CreateParameter("@IsEnabled", isEnabled),
                SqliteHelper.CreateParameter("@TcpIp", tcpIp),
                SqliteHelper.CreateParameter("@TcpPort", tcpPort),
                SqliteHelper.CreateParameter("@ResetPort", resetPort),
                SqliteHelper.CreateParameter("@SerialPortName", serialPortName),
                SqliteHelper.CreateParameter("@SerialBaudRate", serialBaudRate),
                SqliteHelper.CreateParameter("@UpdateTime", DateTime.Now.ToDbTimeString())
            };

            SqliteHelper.ExecuteNonQuery(sql, parameters);
        }

        // ============================================================
        // 2. Reads
        // ============================================================

        /// <summary>
        /// Read all config rows (TCP + serial)
        /// [Notes] No SELECT * (non-negotiable); rows are returned in ascending ConnType order (TCP first).
        /// </summary>
        public static List<PrinterConfig> GetAll()
        {
            List<PrinterConfig> list = new List<PrinterConfig>();

            string sql = @"SELECT Id, ConnType, IsEnabled, TcpIp, TcpPort, ResetPort,
                                  SerialPortName, SerialBaudRate, UpdateTime
                           FROM PrinterConfig ORDER BY ConnType ASC;";

            DataTable table = SqliteHelper.ExecuteQuery(sql);

            foreach (DataRow row in table.Rows)
            {
                list.Add(MapRow(row));
            }

            return list;
        }

        /// <summary>
        /// Read the currently enabled row's configuration (returns null when no enabled row is found, letting the BLL
        /// prompt "configure it first")
        /// [Notes] LIMIT 1 —— normally at most one of the two rows is enabled; multiple enabled rows is dirty data, so
        /// the first row is taken as a fallback.
        /// </summary>
        public static PrinterConfig? GetEnabled()
        {
            string sql = @"SELECT Id, ConnType, IsEnabled, TcpIp, TcpPort, ResetPort,
                                  SerialPortName, SerialBaudRate, UpdateTime
                           FROM PrinterConfig WHERE IsEnabled = 1 LIMIT 1;";

            DataTable table = SqliteHelper.ExecuteQuery(sql);

            if (table.Rows.Count == 0)
            {
                return null;
            }

            return MapRow(table.Rows[0]);
        }

        /// <summary>DataTable row -> entity (hand-written mapping, avoiding the compatibility issues a reflection-based converter has with enum properties)</summary>
        private static PrinterConfig MapRow(DataRow row)
        {
            PrinterConfig config = new PrinterConfig();
            config.Id = row["Id"].ToInt();
            config.ConnType = (ConnType)row["ConnType"].ToInt((int)ConnType.Tcp);
            config.IsEnabled = row["IsEnabled"].ToInt();
            config.TcpIp = row["TcpIp"].ToSafeString();
            config.TcpPort = row["TcpPort"].ToInt(7000);
            config.ResetPort = row["ResetPort"].ToInt(700);
            config.SerialPortName = row["SerialPortName"].ToSafeString();
            config.SerialBaudRate = row["SerialBaudRate"].ToInt(9600);
            config.UpdateTime = row["UpdateTime"].ToSafeString();
            return config;
        }

        // ============================================================
        // 3. Save (single transaction: both rows' parameters + mutually exclusive enable switch)
        // ============================================================

        /// <summary>
        /// Save the configuration (completed in a single transaction: update each row's parameters + switch the enabled flag onto the row matching enabledType)
        /// [Transaction guarantee] First clear IsEnabled on all rows, then enable the target row -- a failure at any step rolls everything back,
        ///   so no intermediate state with both rows enabled or both rows disabled can occur.
        /// </summary>
        /// <param name="tcpConfig">TCP row config (parameters are saved whether or not it is enabled)</param>
        /// <param name="serialConfig">Serial row config (parameters are saved whether or not it is enabled)</param>
        /// <param name="enabledType">Connection type to enable after saving</param>
        public static void Save(PrinterConfig tcpConfig, PrinterConfig serialConfig, ConnType enabledType)
        {
            if (tcpConfig == null || serialConfig == null)
            {
                throw new ArgumentException("The TCP or serial config object is null; cannot save");
            }

            string now = DateTime.Now.ToDbTimeString();

            SqliteHelper.ExecuteInTransaction(delegate(SqliteConnection connection, SqliteTransaction transaction)
            {
                // 1. Clear both rows' IsEnabled flags (preparing for the mutually exclusive switch)
                using (SqliteCommand cmdClear = SqliteHelper.CreateCommand(connection, transaction,
                           "UPDATE PrinterConfig SET IsEnabled = 0;"))
                {
                    cmdClear.ExecuteNonQuery();
                }

                // 2. Update the TCP row parameters and enable it if required
                const string sqlTcp = @"UPDATE PrinterConfig
                                        SET TcpIp = @TcpIp, TcpPort = @TcpPort, ResetPort = @ResetPort,
                                            IsEnabled = @IsEnabled, UpdateTime = @UpdateTime
                                        WHERE ConnType = @ConnType;";

                using (SqliteCommand cmdTcp = SqliteHelper.CreateCommand(connection, transaction, sqlTcp))
                {
                    cmdTcp.Parameters.Add(SqliteHelper.CreateParameter("@TcpIp", tcpConfig.TcpIp));
                    cmdTcp.Parameters.Add(SqliteHelper.CreateParameter("@TcpPort", tcpConfig.TcpPort));
                    cmdTcp.Parameters.Add(SqliteHelper.CreateParameter("@ResetPort", tcpConfig.ResetPort));
                    cmdTcp.Parameters.Add(SqliteHelper.CreateParameter("@IsEnabled",
                        enabledType == ConnType.Tcp ? 1 : 0));
                    cmdTcp.Parameters.Add(SqliteHelper.CreateParameter("@UpdateTime", now));
                    cmdTcp.Parameters.Add(SqliteHelper.CreateParameter("@ConnType", (int)ConnType.Tcp));
                    cmdTcp.ExecuteNonQuery();
                }

                // 3. Update the serial row's parameters and enable it as needed
                const string sqlSerial = @"UPDATE PrinterConfig
                                           SET SerialPortName = @SerialPortName, SerialBaudRate = @SerialBaudRate,
                                               IsEnabled = @IsEnabled, UpdateTime = @UpdateTime
                                           WHERE ConnType = @ConnType;";

                using (SqliteCommand cmdSerial = SqliteHelper.CreateCommand(connection, transaction, sqlSerial))
                {
                    cmdSerial.Parameters.Add(SqliteHelper.CreateParameter("@SerialPortName", serialConfig.SerialPortName));
                    cmdSerial.Parameters.Add(SqliteHelper.CreateParameter("@SerialBaudRate", serialConfig.SerialBaudRate));
                    cmdSerial.Parameters.Add(SqliteHelper.CreateParameter("@IsEnabled",
                        enabledType == ConnType.Serial ? 1 : 0));
                    cmdSerial.Parameters.Add(SqliteHelper.CreateParameter("@UpdateTime", now));
                    cmdSerial.Parameters.Add(SqliteHelper.CreateParameter("@ConnType", (int)ConnType.Serial));
                    cmdSerial.ExecuteNonQuery();
                }
            });
        }
    }
}

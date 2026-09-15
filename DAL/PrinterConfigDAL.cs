using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;
using InkPrinterCode.Model;
using InkPrinterCode.Model.Enums;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] 喷码机连接配置数据访问类（PrinterConfig 表，TCP/串口双行设计）
    ///
    /// 【双行机制（万总要求：二选一可来回切换，切换时配置内容不丢，只改启用标志）】
    ///   - 表中固定两行：ConnType=0（TCP）一行、ConnType=1（串口）一行，各存各的参数；
    ///   - IsEnabled=1 的行是"当前启用"的连接方式，两行中最多一行启用；
    ///   - 保存 = 单事务内：两行参数各自 UPDATE（内容互不覆盖）+ 启用标志互斥切换。
    ///
    /// 【红线】只读不改表结构；全参数化；启用切换在单事务内完成（绝不出现两行同时启用
    ///   或同时禁用的中间状态）。
    /// </summary>
    public static class PrinterConfigDAL
    {
        // ============================================================
        // 1. 行保障
        // ============================================================

        /// <summary>
        /// 确保表里有 TCP / 串口两行配置（首次启动播种，之后幂等跳过）
        /// 【说明】程序启动时由 DbInitializer 调用；配置窗体打开前若仍无行也会兜底调用。
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

        /// <summary>按连接类型插入一行配置（行已存在则跳过）</summary>
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
        // 2. 读取
        // ============================================================

        /// <summary>
        /// 读取全部配置行（TCP + 串口）
        /// 【说明】不用 SELECT *（红线）；按 ConnType 升序返回（TCP 在前）。
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
        /// 读取当前启用行的配置（找不到启用行时返回 null，由 BLL 提示"先配置"）
        /// 【说明】LIMIT 1 —— 正常情况两行中最多一行启用，多行启用属于脏数据，取第一行兜底。
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

        /// <summary>DataTable 行 → 实体（手写映射，避免反射版转换对枚举属性的兼容问题）</summary>
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
        // 3. 保存（单事务：两行参数 + 启用标志互斥切换）
        // ============================================================

        /// <summary>
        /// 保存配置（单事务内完成：两行参数各自更新 + 启用标志切换到 enabledType 那一行）
        /// 【事务保证】先全部 IsEnabled 清零，再启用目标行 —— 任一步失败整体回滚，
        ///   不会出现"两行同时启用"或"两行同时禁用"的中间状态。
        /// </summary>
        /// <param name="tcpConfig">TCP 行配置（参数会被保存，无论是否启用）</param>
        /// <param name="serialConfig">串口行配置（参数会被保存，无论是否启用）</param>
        /// <param name="enabledType">保存后启用的连接类型</param>
        public static void Save(PrinterConfig tcpConfig, PrinterConfig serialConfig, ConnType enabledType)
        {
            if (tcpConfig == null || serialConfig == null)
            {
                throw new ArgumentException("TCP 或串口配置对象为空，无法保存");
            }

            string now = DateTime.Now.ToDbTimeString();

            SqliteHelper.ExecuteInTransaction(delegate(SqliteConnection connection, SqliteTransaction transaction)
            {
                // 1. 两行 IsEnabled 全部清零（为互斥切换做准备）
                using (SqliteCommand cmdClear = SqliteHelper.CreateCommand(connection, transaction,
                           "UPDATE PrinterConfig SET IsEnabled = 0;"))
                {
                    cmdClear.ExecuteNonQuery();
                }

                // 2. 更新 TCP 行参数并按需启用
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

                // 3. 更新串口行参数并按需启用
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

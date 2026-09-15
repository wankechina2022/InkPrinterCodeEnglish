using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] SQLite 数据访问基础类 —— 封装连接创建、参数化执行、查询、事务
    ///
    /// 【为什么不用规约里的 SqlHelper(SqlClient)】本项目数据库是 SQLite，
    ///   封装形态照抄规约：参数化查询 + using 释放 + ExecuteQuery/NonQuery/Scalar + 事务批量提交，
    ///   只是底层驱动换成 Microsoft.Data.Sqlite。
    ///
    /// 【三条硬约束（开发规约红线）】
    ///   1. 所有 SQL 一律参数化，禁止字符串拼接值 —— 既防注入，也避免码值里带引号把 SQL 撑坏；
    ///   2. 连接、命令、读取器全部 using 释放，不手动 Close，防止异常路径漏释放导致 SQLite 文件被锁；
    ///   3. 本类只做通用执行，不含任何建表 / 删表语句（DDL 全部集中在 DbInitializer，且只用 IF NOT EXISTS）。
    ///
    /// 【边界】
    ///   - CreateConnection 会先确保数据库目录存在（目录不存在时 Sqlite 打开文件会直接失败）；
    ///   - ExecuteScalar 无结果时返回 null，调用方统一用 Extensions 的 ToLong/ToInt 兜底；
    ///   - 事务回调里抛出的任何异常都会先回滚再原样抛出，调用方可以放心在 catch 里写日志。
    /// </summary>
    public static class SqliteHelper
    {
        // ============================================================
        // 1. 连接
        // ============================================================

        /// <summary>
        /// 创建并打开一个数据库连接
        /// 【说明】每次调用都新建连接 —— SQLite 是文件库，连接创建开销极小；
        ///   用完即释放（using）比长期持有一个静态连接更安全，不会出现"文件被自己锁住"的怪问题。
        /// </summary>
        public static SqliteConnection CreateConnection()
        {
            // 目录不存在时自动创建（开发规约：写文件前目录不存在自动创建）
            ValidateHelper.EnsureFolderExists(ConfigHelper.DbFolderPath);

            SqliteConnection connection = new SqliteConnection(ConfigHelper.ConnectionString);
            connection.Open();
            return connection;
        }

        // ============================================================
        // 2. 参数
        // ============================================================

        /// <summary>
        /// 创建参数。null 统一转成 DBNull，库中存 NULL 而不是字符串 "null"
        /// </summary>
        public static SqliteParameter CreateParameter(string name, object? value)
        {
            if (value == null)
            {
                return new SqliteParameter(name, DBNull.Value);
            }
            return new SqliteParameter(name, value);
        }

        /// <summary>
        /// 把参数数组挂到命令上
        /// 【注意】SqliteParameter 实例不能同时被多个命令共用，每次执行都必须新建，
        ///   因此本方法只做"添加"，不做缓存复用。
        /// </summary>
        public static void AddParameters(SqliteCommand command, SqliteParameter[]? parameters)
        {
            if (command == null)
            {
                return;
            }
            if (parameters == null)
            {
                return;
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                command.Parameters.Add(parameters[i]);
            }
        }

        // ============================================================
        // 3. 执行（自带连接）
        // ============================================================

        /// <summary>
        /// 执行增删改，返回受影响行数
        /// </summary>
        public static int ExecuteNonQuery(string sql, SqliteParameter[]? parameters = null)
        {
            using (SqliteConnection connection = CreateConnection())
            {
                using (SqliteCommand command = new SqliteCommand(sql, connection))
                {
                    AddParameters(command, parameters);
                    return command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// 执行查询并返回第一行第一列
        /// 【用途】COUNT(1) 统计、last_insert_rowid() 取自增主键。
        /// 【边界】无结果时返回 null，调用方须用 Extensions.ToInt / ToLong 兜底，不要直接强转。
        /// </summary>
        public static object? ExecuteScalar(string sql, SqliteParameter[]? parameters = null)
        {
            using (SqliteConnection connection = CreateConnection())
            {
                using (SqliteCommand command = new SqliteCommand(sql, connection))
                {
                    AddParameters(command, parameters);
                    return command.ExecuteScalar();
                }
            }
        }

        /// <summary>
        /// 执行查询并返回 DataTable
        /// 【机制】手写列与行的填充，不用 DataTable.Load ——
        ///   Load 会按 SQLite 的声明类型做映射（INTEGER → long、TEXT → string），
        ///   手写统一按 object 承接，交调用方用 Extensions.ToXxx 决定怎么转，行为更可预期。
        /// </summary>
        public static DataTable ExecuteQuery(string sql, SqliteParameter[]? parameters = null)
        {
            DataTable table = new DataTable();

            using (SqliteConnection connection = CreateConnection())
            {
                using (SqliteCommand command = new SqliteCommand(sql, connection))
                {
                    AddParameters(command, parameters);

                    using (SqliteDataReader reader = command.ExecuteReader())
                    {
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            table.Columns.Add(reader.GetName(i), typeof(object));
                        }

                        while (reader.Read())
                        {
                            object[] values = new object[reader.FieldCount];
                            reader.GetValues(values);
                            table.Rows.Add(values);
                        }
                    }
                }
            }

            return table;
        }

        // ============================================================
        // 4. 执行（外部传入连接与事务，供批量操作使用）
        // ============================================================

        /// <summary>
        /// 在已有连接与事务上创建命令
        /// </summary>
        public static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            SqliteCommand command = new SqliteCommand(sql, connection);
            command.Transaction = transaction;
            return command;
        }

        /// <summary>
        /// 在已有连接上执行（不参与事务，用于事务外的简单查询）
        /// </summary>
        public static object? ExecuteScalar(SqliteConnection connection, string sql, SqliteParameter[]? parameters = null)
        {
            using (SqliteCommand command = new SqliteCommand(sql, connection))
            {
                AddParameters(command, parameters);
                return command.ExecuteScalar();
            }
        }

        // ============================================================
        // 5. 事务
        // ============================================================

        /// <summary>
        /// 在一个事务里执行一批数据库操作
        ///
        /// 【机制】
        ///   - 成功：回调执行完直接 Commit；
        ///   - 失败：先尝试 Rollback（回滚失败只记日志，不能把原始异常吞掉），再把原始异常原样抛给调用方。
        ///
        /// 【为什么用回调而不是把连接/事务暴露出去】
        ///   回调能保证"开了事务必定收尾"，调用方不可能忘了 Commit 或忘了释放连接。
        ///
        /// 【边界】回调内部不要弹 MessageBox —— 弹框会阻塞，事务长时间不提交会把整个库文件锁住。
        /// </summary>
        public static void ExecuteInTransaction(Action<SqliteConnection, SqliteTransaction> action)
        {
            using (SqliteConnection connection = CreateConnection())
            {
                using (SqliteTransaction transaction = connection.BeginTransaction())
                {
                    try
                    {
                        action(connection, transaction);
                        transaction.Commit();
                    }
                    catch (Exception)
                    {
                        try
                        {
                            transaction.Rollback();
                        }
                        catch (Exception exRollback)
                        {
                            // 回滚失败也要把原始异常抛出去，这里只记日志
                            LogHelper.Instance.Error("事务回滚失败", exRollback);
                        }
                        throw;
                    }
                }
            }
        }
    }
}

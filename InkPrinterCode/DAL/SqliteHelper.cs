using System.Data;
using Microsoft.Data.Sqlite;
using InkPrinterCode.Common;

namespace InkPrinterCode.DAL
{
    /// <summary>
    /// [2026-09-10] SQLite base data access class -- wraps connection creation, parameterised execution, queries and transactions
    ///
    /// [Why the standard SqlHelper(SqlClient) is not used] This project's database is SQLite, so the wrapper follows the same shape as the
    ///   standard: parameterised queries + using-based disposal + ExecuteQuery/NonQuery/Scalar + batched transaction commits,
    ///   with only the underlying driver swapped for Microsoft.Data.Sqlite.
    ///
    /// [Three hard constraints (non-negotiable development rules)]
    ///   1. All SQL is parameterised and value concatenation is forbidden -- this prevents injection and stops quotes in code values from breaking the SQL;
    ///   2. Connections, commands and readers are all disposed with using and never manually closed, so exception paths cannot leak a handle and lock the SQLite file;
    ///   3. This class only performs generic execution and contains no CREATE/DROP statements (all DDL lives in DbInitializer and only uses IF NOT EXISTS).
    ///
    /// [Boundaries]
    ///   - CreateConnection first ensures the database directory exists (Sqlite fails outright when opening a file in a missing directory);
    ///   - ExecuteScalar returns null when there is no result; callers uniformly fall back to Extensions.ToLong/ToInt;
    ///   - Any exception thrown inside a transaction callback is rolled back first and then rethrown unchanged, so callers can safely log in their catch block.
    /// </summary>
    public static class SqliteHelper
    {
        // ============================================================
        // 1. Connections
        // ============================================================

        /// <summary>
        /// Create and open a database connection
        /// [Notes] A new connection is created on every call -- SQLite is a file database and connection creation is extremely cheap;
        ///   disposing immediately after use (using) is safer than holding a long-lived static connection, which can produce the odd problem of the file being locked by the process itself.
        /// </summary>
        public static SqliteConnection CreateConnection()
        {
            // Create the directory automatically when missing (development rule: create the directory automatically before writing a file)
            ValidateHelper.EnsureFolderExists(ConfigHelper.DbFolderPath);

            SqliteConnection connection = new SqliteConnection(ConfigHelper.ConnectionString);
            connection.Open();
            return connection;
        }

        // ============================================================
        // 2. Parameters
        // ============================================================

        /// <summary>
        /// Create a parameter. null is uniformly converted to DBNull so the database stores NULL rather than the string "null"
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
        /// Attach a parameter array to a command
        /// [Caution] A SqliteParameter instance cannot be shared by several commands at the same time and must be created anew for each execution,
        ///   so this method only adds parameters and never caches or reuses them.
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
        // 3. Execution (uses its own connection)
        // ============================================================

        /// <summary>
        /// Execute an insert, update or delete and return the number of affected rows
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
        /// Execute a query and return the first column of the first row
        /// [Purpose] COUNT(1) statistics and last_insert_rowid() to obtain an auto-increment primary key.
        /// [Boundaries] It returns null when there is no result; the caller must use Extensions.ToInt / ToLong as the
        /// fallback rather than casting directly.
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
        /// Execute a query and return a DataTable
        /// [Mechanism] Columns and rows are filled by hand rather than with DataTable.Load --
        ///   Load maps by SQLite's declared types (INTEGER -> long, TEXT -> string), whereas manual filling
        ///   uniformly takes everything as object and lets the caller decide how to convert it via Extensions.ToXxx, which is far more predictable.
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
        // 4. Execution (with a connection and transaction passed in, for bulk operations)
        // ============================================================

        /// <summary>
        /// Create a command on an existing connection and transaction
        /// </summary>
        public static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            SqliteCommand command = new SqliteCommand(sql, connection);
            command.Transaction = transaction;
            return command;
        }

        /// <summary>
        /// Execute on an existing connection (not part of a transaction, for simple queries outside one)
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
        // 5. Transactions
        // ============================================================

        /// <summary>
        /// Execute a batch of database operations inside one transaction
        ///
        /// [Mechanism]
        ///   - Success: the callback finishes and the transaction is committed directly;
        ///   - Failure: Rollback is attempted first (a rollback failure is only logged and must not swallow the original exception), then the original exception is rethrown unchanged to the caller.
        ///
        /// [Why a callback is used instead of exposing the connection/transaction]
        ///   A callback guarantees that any opened transaction is always finalised, so a caller cannot forget to Commit or to release the connection.
        ///
        /// [Boundary] Do not show a MessageBox inside the callback -- a modal dialog blocks, and a transaction left uncommitted for a long time locks the whole database file.
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
                            // Even if the rollback fails the original exception must still be thrown; here we only log it
                            LogHelper.Instance.Error("Transaction rollback failed", exRollback);
                        }
                        throw;
                    }
                }
            }
        }
    }
}

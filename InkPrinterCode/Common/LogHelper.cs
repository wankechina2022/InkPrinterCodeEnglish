using System.Globalization;
using System.Text;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Logging utility class —— a global singleton that writes runtime logs to the Logs folder
    /// under the program directory
    ///
    /// [Three kinds of log files]
    ///   1) app_yyyyMMdd.log       The full log; DEBUG~FATAL are all written (subject to the minimum-level filter)
    ///   2) error_yyyyMMdd.log     A copy of ERROR / FATAL entries (with exception information and stack); when
    ///                             troubleshooting, this is the only file to look at
    ///   3) duplicate_yyyyMMdd.log Duplicate-code details from imports, isolated separately
    ///      —— a single import of 100,000 records may produce tens of thousands of duplicates, and writing them all
    ///         into the main log would drown out the useful information, so the details go into their own file and
    ///         the count is capped by the limit passed to BeginDuplicateSession; beyond that only the summary is written.
    ///
    /// [Mechanism]
    ///   - Singleton + serialized writes under a lock: imports run on a background thread and the UI runs on the UI
    ///     thread, and both may write logs; without locking, the contents of the same log line would interleave.
    ///   - Lazy cleanup: the first log write performs the expiry cleanup once (attempted only once, success or
    ///     failure), so that the directory is not scanned on every write.
    ///   - Level filtering: anything below the configured minimum level is discarded outright, reducing useless IO.
    ///   - File encoding is fixed to UTF-8 with BOM, so Chinese text does not become garbled when opened in Notepad
    ///     or Excel.
    ///
    /// [Boundaries (important)]
    ///   - None of this class's public methods throw outward. Directory creation failure, a full disk, a file in use,
    ///     an illegal path…… all are caught internally and turned into Debug output. Logging is a supporting facility
    ///     and must never be allowed to crash a business flow because "writing the log failed".
    ///   - Expiry cleanup only touches files directly under its own Logs directory whose name looks like
    ///     "prefix_yyyyMMdd.log"; it does not recurse into subdirectories and does not touch files with any other
    ///     name (safety red line: never delete a file that does not belong to this component by mistake).
    ///   - When EnableLogging=false, all write methods return immediately.
    /// </summary>
    public sealed class LogHelper
    {
        /// <summary>
        /// Log level. The larger the value, the more severe, used for comparison filtering against the configured
        /// minimum level.
        /// </summary>
        public enum LogLevel
        {
            /// <summary>Debug information; high volume, generally not enabled in production</summary>
            DEBUG = 0,
            /// <summary>Normal flow information</summary>
            INFO = 1,
            /// <summary>Warning; the business can continue (for example an import hitting a duplicate code)</summary>
            WARN = 2,
            /// <summary>Error; the current operation failed</summary>
            ERROR = 3,
            /// <summary>Fatal error; the program may be unable to continue</summary>
            FATAL = 4
        }

        // ============================================================
        // 1. Private fields
        // ============================================================

        private static readonly LogHelper _instance = new LogHelper();

        /// <summary>File write lock. Every disk write must happen inside this lock</summary>
        private readonly object _lockObj = new object();

        /// <summary>Full path of the log directory. An empty string means the directory is unavailable, in which case all writes are silently skipped</summary>
        private readonly string _logDirectory = string.Empty;

        /// <summary>Whether the expired-log cleanup has already been attempted (only attempted once)</summary>
        private bool _cleanupExecuted = false;

        /// <summary>Cached minimum log level</summary>
        private LogLevel _minLevel = LogLevel.INFO;

        /// <summary>Whether the minimum level has been parsed already (parsed once and then cached; reset by RefreshMinLevel when the configuration is saved)</summary>
        private bool _minLevelParsed = false;

        // ---- Duplicate-code session state (the methods Begin/Log/End are used as a set) ----

        /// <summary>Whether a duplicate-code recording session is currently open</summary>
        private bool _duplicateSessionOpen = false;

        /// <summary>Upper limit on the number of details that may be written in this session</summary>
        private int _duplicateLimit = 0;

        /// <summary>Number of details already written in this session</summary>
        private int _duplicateWritten = 0;

        // ============================================================
        // 2. Constructor and singleton entry point
        // ============================================================

        /// <summary>
        /// Private constructor —— determines the log directory and makes sure it exists.
        /// [Boundaries] If directory creation fails, _logDirectory is set to empty and all subsequent writes are
        /// silently skipped, without affecting program startup.
        /// </summary>
        private LogHelper()
        {
            try
            {
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                _logDirectory = directory;
            }
            catch (Exception ex)
            {
                _logDirectory = string.Empty;
                System.Diagnostics.Debug.WriteLine("LogHelper initialization failed; logging has been disabled: " + ex.Message);
            }
        }

        /// <summary>The one and only global instance</summary>
        public static LogHelper Instance
        {
            get { return _instance; }
        }

        /// <summary>Full path of the log directory (used by UI features such as "open log directory"); empty when unavailable</summary>
        public string LogDirectory
        {
            get { return _logDirectory; }
        }

        // ============================================================
        // 3. Public write methods
        // ============================================================

        /// <summary>
        /// Write a single log entry
        /// </summary>
        /// <param name="level">Log level</param>
        /// <param name="message">Log content; null is treated as an empty string</param>
        /// <param name="ex">Associated exception, may be null; when non-null the exception information and stack are appended</param>
        public void Log(LogLevel level, string? message, Exception? ex = null)
        {
            try
            {
                if (!ConfigHelper.EnableLogging)
                {
                    return;
                }
                if (string.IsNullOrEmpty(_logDirectory))
                {
                    return;
                }
                if ((int)level < (int)GetMinLevel())
                {
                    return;
                }

                string dateTag = DateTime.Now.ToString("yyyyMMdd");
                string content = BuildContent(level, message, ex);

                lock (_lockObj)
                {
                    EnsureCleanupOnce();

                    AppendToFile("app_" + dateTag + ".log", content);

                    // ERROR / FATAL also write a copy into the error file, making it easy to review errors only
                    if (level == LogLevel.ERROR || level == LogLevel.FATAL)
                    {
                        AppendToFile("error_" + dateTag + ".log", content);
                    }
                }
            }
            catch (Exception exSelf)
            {
                // Fallback: any problem in the logging component itself must not affect the business flow
                System.Diagnostics.Debug.WriteLine("Internal exception in LogHelper.Log: " + exSelf.Message);
            }
        }

        /// <summary>Write a DEBUG-level log</summary>
        public void Debug(string? message)
        {
            Log(LogLevel.DEBUG, message, null);
        }

        /// <summary>Write an INFO-level log</summary>
        public void Info(string? message)
        {
            Log(LogLevel.INFO, message, null);
        }

        /// <summary>Write a WARN-level log</summary>
        public void Warn(string? message)
        {
            Log(LogLevel.WARN, message, null);
        }

        /// <summary>Write an ERROR-level log (also written to the error file)</summary>
        public void Error(string? message, Exception? ex = null)
        {
            Log(LogLevel.ERROR, message, ex);
        }

        /// <summary>Write a FATAL-level log (also written to the error file)</summary>
        public void Fatal(string? message, Exception? ex = null)
        {
            Log(LogLevel.FATAL, message, ex);
        }

        /// <summary>
        /// Reset the cached minimum log level (added [2026-09-10])
        /// [Purpose] Called by SystemConfigBLL after the system-parameter configuration form is saved successfully,
        ///   so that a change to MinLogLevel takes effect immediately —— the next log write re-parses it from
        ///   ConfigHelper, with no need to restart the program.
        /// [Boundaries] The method itself has no side effects and is safe to call concurrently from multiple threads.
        /// </summary>
        public void RefreshMinLevel()
        {
            _minLevelParsed = false;
        }

        // ============================================================
        // 4. Duplicate-code detail session (for the import feature only)
        // ============================================================

        /// <summary>
        /// Open a duplicate-code recording session (called once at the start of each import)
        /// </summary>
        /// <param name="detailLimit">Upper limit on the number of details allowed in this session; &lt;=0 means write no details, only the summary</param>
        public void BeginDuplicateSession(int detailLimit)
        {
            try
            {
                lock (_lockObj)
                {
                    _duplicateSessionOpen = true;
                    _duplicateWritten = 0;
                    if (detailLimit < 0)
                    {
                        _duplicateLimit = 0;
                    }
                    else
                    {
                        _duplicateLimit = detailLimit;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Internal exception in LogHelper.BeginDuplicateSession: " + ex.Message);
            }
        }

        /// <summary>
        /// Record one duplicate-code detail
        /// [Boundaries] It returns silently when no session is open, when the limit has been exceeded, or when
        /// logging is disabled; the caller does not need to check any of that.
        /// </summary>
        /// <param name="codeValue">The duplicated code value</param>
        /// <param name="rowNo">Line number in the source file (starting from 1)</param>
        /// <param name="source">Explanation of the duplicate source, for example "duplicate within the file" or "already exists in the database"</param>
        public void LogDuplicate(string? codeValue, int rowNo, string? source)
        {
            try
            {
                if (!ConfigHelper.EnableLogging)
                {
                    return;
                }
                if (string.IsNullOrEmpty(_logDirectory))
                {
                    return;
                }

                lock (_lockObj)
                {
                    if (!_duplicateSessionOpen)
                    {
                        return;
                    }
                    if (_duplicateWritten >= _duplicateLimit)
                    {
                        return;
                    }
                    _duplicateWritten++;

                    string safeCode = codeValue ?? string.Empty;
                    string safeSource = source ?? string.Empty;
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] Row="
                                  + rowNo.ToString() + "  Code=" + safeCode + "  Source=" + safeSource;

                    AppendToFile("duplicate_" + DateTime.Now.ToString("yyyyMMdd") + ".log", line);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Internal exception in LogHelper.LogDuplicate: " + ex.Message);
            }
        }

        /// <summary>
        /// End a duplicate-code recording session, writing the summary line and closing the session
        /// </summary>
        /// <param name="summary">Summary description, for example "file xxx.txt had 87432 duplicates in total (12 within the file, 87420 already in the database)"</param>
        public void EndDuplicateSession(string? summary)
        {
            try
            {
                if (!ConfigHelper.EnableLogging)
                {
                    return;
                }
                if (string.IsNullOrEmpty(_logDirectory))
                {
                    return;
                }

                lock (_lockObj)
                {
                    if (!_duplicateSessionOpen)
                    {
                        return;
                    }

                    string safeSummary = summary ?? string.Empty;
                    string fileName = "duplicate_" + DateTime.Now.ToString("yyyyMMdd") + ".log";
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] [Import Summary] "
                                  + safeSummary + "; details recorded: " + _duplicateWritten.ToString()
                                  + " (limit " + _duplicateLimit.ToString() + ")";

                    AppendToFile(fileName, line);
                    AppendToFile(fileName, "--------------------------------------------------");

                    _duplicateSessionOpen = false;
                    _duplicateWritten = 0;
                    _duplicateLimit = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Internal exception in LogHelper.EndDuplicateSession: " + ex.Message);
            }
        }

        // ============================================================
        // 5. Private helper methods
        // ============================================================

        /// <summary>
        /// Parse and cache the minimum log level
        /// [Notes] Once parsed, the cached value is used from then on —— log writes are extremely frequent and there
        ///   is no need to parse the string every time. When the configuration is saved, SystemConfigBLL calls
        ///   RefreshMinLevel to reset the cache flag, and the next log entry uses the new level.
        /// [Threading] This method may be called concurrently from outside the lock, but two threads compute exactly
        ///   the same result (idempotent assignment), so there are no side effects.
        /// </summary>
        private LogLevel GetMinLevel()
        {
            if (_minLevelParsed)
            {
                return _minLevel;
            }

            string text = ConfigHelper.MinLogLevelText.ToUpperInvariant();
            switch (text)
            {
                case "DEBUG":
                    _minLevel = LogLevel.DEBUG;
                    break;
                case "INFO":
                    _minLevel = LogLevel.INFO;
                    break;
                case "WARN":
                    _minLevel = LogLevel.WARN;
                    break;
                case "ERROR":
                    _minLevel = LogLevel.ERROR;
                    break;
                case "FATAL":
                    _minLevel = LogLevel.FATAL;
                    break;
                default:
                    // If the configuration is written incorrectly, treat it as INFO; this does not affect operation
                    _minLevel = LogLevel.INFO;
                    break;
            }

            _minLevelParsed = true;
            return _minLevel;
        }

        /// <summary>
        /// Assemble one log line: time + level + thread id + content (+ exception information and stack)
        /// [Purpose of the thread id] Imports run on a background thread and the UI runs on the UI thread; when a
        /// problem occurs, the thread id tells you which code path it came from.
        /// </summary>
        private string BuildContent(LogLevel level, string? message, Exception? ex)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("[");
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            builder.Append("] [");
            builder.Append(level.ToString());
            builder.Append("] [T");
            builder.Append(Environment.CurrentManagedThreadId.ToString());
            builder.Append("] ");

            if (message == null)
            {
                builder.Append(string.Empty);
            }
            else
            {
                builder.Append(message);
            }

            if (ex != null)
            {
                builder.AppendLine();
                builder.Append("    Exception Type: ");
                builder.Append(ex.GetType().FullName);
                builder.AppendLine();
                builder.Append("    Exception Message: ");
                builder.Append(ex.Message);
                builder.AppendLine();
                builder.Append("    Stack Trace: ");
                if (ex.StackTrace == null)
                {
                    builder.Append("(no stack trace)");
                }
                else
                {
                    builder.Append(ex.StackTrace);
                }

                // Include the inner exception too; the real cause from SQLite / NPOI is often hidden in InnerException
                if (ex.InnerException != null)
                {
                    builder.AppendLine();
                    builder.Append("    Inner Exception: ");
                    builder.Append(ex.InnerException.Message);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Append one line of content to the specified log file
        /// [Boundaries] A failed disk write (full disk, exclusively locked file, illegal path) only records Debug and
        /// does not throw.
        /// [Encoding] UTF-8 with BOM —— when the file does not exist, AppendAllText creates it and writes the BOM,
        /// which keeps non-ASCII text from becoming garbled.
        /// </summary>
        private void AppendToFile(string fileName, string content)
        {
            try
            {
                string fullPath = Path.Combine(_logDirectory, fileName);
                File.AppendAllText(fullPath, content + Environment.NewLine, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LogHelper failed to write log file [" + fileName + "]: " + ex.Message);
            }
        }

        /// <summary>
        /// Clean up expired logs (attempted only once)
        ///
        /// [Safety constraints (important)]
        ///   1. Only the single level of _logDirectory is processed, with SearchOption.TopDirectoryOnly, never recursively;
        ///   2. Only files with the .log extension whose name looks like "prefix_yyyyMMdd" are processed; if the date
        ///      segment cannot be parsed, the file is skipped;
        ///   3. Failure to delete one file does not affect the others (each has its own try/catch);
        ///   4. When LogKeepDays=0 it returns immediately and performs no deletion at all.
        /// [Call site] It must be called inside the _lockObj lock.
        /// </summary>
        private void EnsureCleanupOnce()
        {
            if (_cleanupExecuted)
            {
                return;
            }
            // Whether this cleanup succeeds or fails, it is only attempted once, to avoid repeatedly scanning the directory
            _cleanupExecuted = true;

            try
            {
                int keepDays = ConfigHelper.LogKeepDays;
                if (keepDays <= 0)
                {
                    return;
                }

                DateTime limitDate = DateTime.Now.Date.AddDays(-keepDays);
                string[] files = Directory.GetFiles(_logDirectory, "*.log", SearchOption.TopDirectoryOnly);

                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        string nameWithoutExt = Path.GetFileNameWithoutExtension(files[i]);
                        int underlineIndex = nameWithoutExt.LastIndexOf('_');
                        if (underlineIndex < 0 || underlineIndex + 1 >= nameWithoutExt.Length)
                        {
                            continue;
                        }

                        string datePart = nameWithoutExt.Substring(underlineIndex + 1);
                        if (datePart.Length != 8)
                        {
                            continue;
                        }

                        DateTime fileDate;
                        bool parsed = DateTime.TryParseExact(datePart, "yyyyMMdd",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out fileDate);
                        if (!parsed)
                        {
                            continue;
                        }

                        if (fileDate.Date >= limitDate)
                        {
                            continue;
                        }

                        File.Delete(files[i]);
                    }
                    catch (Exception exOne)
                    {
                        System.Diagnostics.Debug.WriteLine("Failed to clean up expired log [" + files[i] + "]: " + exOne.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to clean up expired logs: " + ex.Message);
            }
        }
    }
}

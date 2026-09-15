using System.Globalization;
using System.Text;

namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] 日志工具类 —— 全局单例，把运行日志落盘到程序目录下的 Logs 文件夹
    ///
    /// 【三类日志文件】
    ///   1) app_yyyyMMdd.log       全量日志，DEBUG~FATAL 都写（受最低级别过滤）
    ///   2) error_yyyyMMdd.log     ERROR / FATAL 的副本（带异常信息与堆栈），排障时只翻这一个
    ///   3) duplicate_yyyyMMdd.log 导入时的重复码明细，单独隔离
    ///      —— 一次导入 10 万条可能出现几万条重复，全写进主日志会把有用信息冲掉，
    ///         所以明细单独成文件，且由 BeginDuplicateSession 传入的上限控制条数，超出只记汇总。
    ///
    /// 【机制】
    ///   - 单例 + lock 串行写：导入在后台线程、界面在 UI 线程，两边都可能写日志，
    ///     不加锁会出现同一行日志内容交错。
    ///   - 惰性清理：首次写日志时执行一次过期清理（只尝试一次，无论成败），避免每次写盘都扫目录。
    ///   - 级别过滤：低于配置的最低级别直接丢弃，减少无用 IO。
    ///   - 文件编码固定 UTF-8 带 BOM，保证用记事本 / Excel 打开中文不乱码。
    ///
    /// 【边界（重要）】
    ///   - 本类所有公开方法都不会向外抛异常。目录创建失败、磁盘写满、文件被占用、路径非法……
    ///     一律内部捕获并转 Debug 输出。日志是辅助设施，绝不允许因为"写日志失败"把业务流程带崩。
    ///   - 清理过期日志时只处理自身 Logs 目录下、文件名形如 "前缀_yyyyMMdd.log" 的文件，
    ///     不递归子目录、不碰任何其他命名的文件（安全红线：绝不误删非本组件文件）。
    ///   - EnableLogging=false 时所有写入方法直接返回。
    /// </summary>
    public sealed class LogHelper
    {
        /// <summary>
        /// 日志级别。数值越大越严重，用于与配置的最低级别做比较过滤。
        /// </summary>
        public enum LogLevel
        {
            /// <summary>调试信息，量大，正式环境一般不开</summary>
            DEBUG = 0,
            /// <summary>常规流程信息</summary>
            INFO = 1,
            /// <summary>警告，业务可继续（例如导入遇到重复码）</summary>
            WARN = 2,
            /// <summary>错误，当前操作失败</summary>
            ERROR = 3,
            /// <summary>致命错误，程序可能无法继续</summary>
            FATAL = 4
        }

        // ============================================================
        // 1. 私有字段
        // ============================================================

        private static readonly LogHelper _instance = new LogHelper();

        /// <summary>写文件锁。所有落盘动作都必须在此锁内进行</summary>
        private readonly object _lockObj = new object();

        /// <summary>日志目录完整路径。为空串表示目录不可用，此时所有写入静默跳过</summary>
        private readonly string _logDirectory = string.Empty;

        /// <summary>过期日志清理是否已尝试过（只尝试一次）</summary>
        private bool _cleanupExecuted = false;

        /// <summary>缓存的最低日志级别</summary>
        private LogLevel _minLevel = LogLevel.INFO;

        /// <summary>最低级别是否已解析过（解析一次后缓存；配置保存时由 RefreshMinLevel 复位）</summary>
        private bool _minLevelParsed = false;

        // ---- 重复码会话状态（Begin/Log/End 三个方法配套使用）----

        /// <summary>当前是否处于一次重复码记录会话中</summary>
        private bool _duplicateSessionOpen = false;

        /// <summary>本次会话允许写入的明细条数上限</summary>
        private int _duplicateLimit = 0;

        /// <summary>本次会话已写入的明细条数</summary>
        private int _duplicateWritten = 0;

        // ============================================================
        // 2. 构造函数与单例入口
        // ============================================================

        /// <summary>
        /// 私有构造 —— 确定日志目录并确保其存在。
        /// 【边界】目录创建失败时把 _logDirectory 置空，后续写入全部静默跳过，不影响程序启动。
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
                System.Diagnostics.Debug.WriteLine("LogHelper 初始化失败，日志功能已停用：" + ex.Message);
            }
        }

        /// <summary>全局唯一实例</summary>
        public static LogHelper Instance
        {
            get { return _instance; }
        }

        /// <summary>日志目录完整路径（供界面上"打开日志目录"之类的功能使用），不可用时为空串</summary>
        public string LogDirectory
        {
            get { return _logDirectory; }
        }

        // ============================================================
        // 3. 公开写入方法
        // ============================================================

        /// <summary>
        /// 写一条日志
        /// </summary>
        /// <param name="level">日志级别</param>
        /// <param name="message">日志内容，传 null 按空串处理</param>
        /// <param name="ex">关联异常，可为 null；非 null 时追加异常信息与堆栈</param>
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

                    // ERROR / FATAL 再往 error 文件写一份副本，方便只看错误
                    if (level == LogLevel.ERROR || level == LogLevel.FATAL)
                    {
                        AppendToFile("error_" + dateTag + ".log", content);
                    }
                }
            }
            catch (Exception exSelf)
            {
                // 兜底：日志组件自身出任何问题都不得影响业务流程
                System.Diagnostics.Debug.WriteLine("LogHelper.Log 内部异常：" + exSelf.Message);
            }
        }

        /// <summary>写 DEBUG 级日志</summary>
        public void Debug(string? message)
        {
            Log(LogLevel.DEBUG, message, null);
        }

        /// <summary>写 INFO 级日志</summary>
        public void Info(string? message)
        {
            Log(LogLevel.INFO, message, null);
        }

        /// <summary>写 WARN 级日志</summary>
        public void Warn(string? message)
        {
            Log(LogLevel.WARN, message, null);
        }

        /// <summary>写 ERROR 级日志（同时写入 error 文件）</summary>
        public void Error(string? message, Exception? ex = null)
        {
            Log(LogLevel.ERROR, message, ex);
        }

        /// <summary>写 FATAL 级日志（同时写入 error 文件）</summary>
        public void Fatal(string? message, Exception? ex = null)
        {
            Log(LogLevel.FATAL, message, ex);
        }

        /// <summary>
        /// 复位最低日志级别缓存（[2026-09-10] 新增）
        /// 【用途】系统参数配置窗体保存成功后由 SystemConfigBLL 调用，让 MinLogLevel
        ///   修改立即生效 —— 下一条日志写入时重新从 ConfigHelper 解析，不用重启程序。
        /// 【边界】方法本身无副作用，多线程并发调用安全。
        /// </summary>
        public void RefreshMinLevel()
        {
            _minLevelParsed = false;
        }

        // ============================================================
        // 4. 重复码明细会话（导入功能专用）
        // ============================================================

        /// <summary>
        /// 开启一次重复码记录会话（每次导入开始时调用一次）
        /// </summary>
        /// <param name="detailLimit">本次允许写入的明细条数上限，&lt;=0 表示不写明细只写汇总</param>
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
                System.Diagnostics.Debug.WriteLine("LogHelper.BeginDuplicateSession 内部异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 记录一条重复码明细
        /// 【边界】未开启会话、超过上限、日志被关闭时静默返回，调用方无需判断。
        /// </summary>
        /// <param name="codeValue">重复的码值</param>
        /// <param name="rowNo">源文件中的行号（从 1 开始）</param>
        /// <param name="source">重复来源说明，例如"文件内重复"或"库内已存在"</param>
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
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] 行号="
                                  + rowNo.ToString() + "  码值=" + safeCode + "  来源=" + safeSource;

                    AppendToFile("duplicate_" + DateTime.Now.ToString("yyyyMMdd") + ".log", line);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LogHelper.LogDuplicate 内部异常：" + ex.Message);
            }
        }

        /// <summary>
        /// 结束一次重复码记录会话，写入汇总行并关闭会话
        /// </summary>
        /// <param name="summary">汇总说明，例如"文件 xxx.txt 共重复 87432 条（文件内 12 条，库内 87420 条）"</param>
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
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] 【本次导入汇总】"
                                  + safeSummary + "；明细已记录 " + _duplicateWritten.ToString()
                                  + " 条（上限 " + _duplicateLimit.ToString() + " 条）";

                    AppendToFile(fileName, line);
                    AppendToFile(fileName, "--------------------------------------------------");

                    _duplicateSessionOpen = false;
                    _duplicateWritten = 0;
                    _duplicateLimit = 0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LogHelper.EndDuplicateSession 内部异常：" + ex.Message);
            }
        }

        // ============================================================
        // 5. 私有辅助方法
        // ============================================================

        /// <summary>
        /// 解析并缓存最低日志级别
        /// 【说明】解析一次后一直用缓存值 —— 日志写入非常频繁，没必要每次都解析字符串。
        ///   配置保存时 SystemConfigBLL 调 RefreshMinLevel 复位缓存标志，下一条日志即用新级别。
        /// 【线程】本方法可能在锁外被并发调用，但两个线程算出的结果完全相同（幂等赋值），无副作用。
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
                    // 配置写错时按 INFO 处理，不影响运行
                    _minLevel = LogLevel.INFO;
                    break;
            }

            _minLevelParsed = true;
            return _minLevel;
        }

        /// <summary>
        /// 组装一条日志文本：时间 + 级别 + 线程号 + 内容（+ 异常信息与堆栈）
        /// 【线程号的用途】导入走后台线程，界面走 UI 线程，出问题时靠线程号能分清是哪条链路。
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
                builder.Append("    异常类型：");
                builder.Append(ex.GetType().FullName);
                builder.AppendLine();
                builder.Append("    异常信息：");
                builder.Append(ex.Message);
                builder.AppendLine();
                builder.Append("    堆栈跟踪：");
                if (ex.StackTrace == null)
                {
                    builder.Append("(无堆栈)");
                }
                else
                {
                    builder.Append(ex.StackTrace);
                }

                // 内层异常也带上，SQLite / NPOI 的真实原因常常藏在 InnerException
                if (ex.InnerException != null)
                {
                    builder.AppendLine();
                    builder.Append("    内层异常：");
                    builder.Append(ex.InnerException.Message);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// 追加一行内容到指定日志文件
        /// 【边界】写盘失败（磁盘满、文件被独占、路径非法）只记 Debug，不抛异常。
        /// 【编码】UTF-8 带 BOM —— 文件不存在时由 AppendAllText 创建并写入 BOM，保证中文不乱码。
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
                System.Diagnostics.Debug.WriteLine("LogHelper 写日志文件[" + fileName + "]失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 清理过期日志（只尝试一次）
        ///
        /// 【安全约束（重要）】
        ///   1. 只处理 _logDirectory 这一层目录，SearchOption.TopDirectoryOnly，绝不递归；
        ///   2. 只处理扩展名 .log 且文件名形如 "前缀_yyyyMMdd" 的文件，日期段解析不出来就跳过；
        ///   3. 单个文件删除失败不影响其他文件（各自 try/catch）；
        ///   4. LogKeepDays=0 时直接返回，不做任何删除动作。
        /// 【调用位置】必须在 _lockObj 锁内调用。
        /// </summary>
        private void EnsureCleanupOnce()
        {
            if (_cleanupExecuted)
            {
                return;
            }
            // 无论本次清理成功还是失败，都只尝试一次，避免反复扫目录
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
                        System.Diagnostics.Debug.WriteLine("清理过期日志[" + files[i] + "]失败：" + exOne.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("清理过期日志失败：" + ex.Message);
            }
        }
    }
}

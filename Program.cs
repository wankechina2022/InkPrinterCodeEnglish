using InkPrinterCode.Common;
using InkPrinterCode.DAL;

namespace InkPrinterCode
{
    /// <summary>
    /// [2026-09-10] 程序入口
    ///
    /// 【本文件负责四件事】
    ///   1. 全局异常兜底：UI 线程异常（ThreadException）+ 非 UI 线程异常（UnhandledException）
    ///      统一记 error 日志并提示用户，杜绝"弹个未处理异常框就退出"或"悄无声息崩掉"；
    ///   2. 单实例限制：用命名 Mutex 防止现场操作员双击多次开出多个实例 ——
    ///      两个实例同时写同一个 SQLite 库文件，极易出现锁冲突和数据错乱；
    ///   3. 数据库初始化：建目录 + 建表（只用 CREATE IF NOT EXISTS，绝不改已存在的表）；
    ///   4. 启动 / 退出日志，便于追溯"程序什么时候开的、什么时候关的"。
    ///
    /// 【为什么保留 ApplicationConfiguration.Initialize()】
    ///   .NET 8 的 WinForms 模板用这一句替代了旧版的 EnableVisualStyles +
    ///   SetCompatibleTextRenderingDefault + SetHighDpiMode，二者等价，手写那三行反而重复。
    /// </summary>
    internal static class Program
    {
        /// <summary>单实例互斥量名称（加 Local\ 前缀，限定在当前 Windows 会话内）</summary>
        private const string MUTEX_NAME = @"Local\InkPrinterCode_SingleInstance_Mutex";

        /// <summary>单实例互斥量，程序退出前必须持有，否则会被 GC 回收导致限制失效</summary>
        private static Mutex? _singleInstanceMutex = null;

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            // ---------- 1. 全局异常兜底（必须最先挂上，越早越好） ----------
            Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
            Application.ApplicationExit += new EventHandler(Application_ApplicationExit);

            // ---------- 2. 单实例检查 ----------
            bool createdNew = false;
            try
            {
                _singleInstanceMutex = new Mutex(true, MUTEX_NAME, out createdNew);
            }
            catch (Exception ex)
            {
                // 创建互斥量失败不阻止程序启动（比如权限受限环境），记日志后继续
                createdNew = true;
                LogHelper.Instance.Warn("创建单实例互斥量失败，本次不做单实例限制：" + ex.Message);
            }

            if (!createdNew)
            {
                MessageHelper.ShowWarning("程序已经在运行中，请勿重复启动。\r\n\r\n如未看到程序窗口，请检查任务栏或任务管理器。");
                return;
            }

            LogHelper.Instance.Info("================ 程序启动 ================");

            // ---------- 3. 数据库初始化 ----------
            if (!DbInitializer.Initialize())
            {
                MessageHelper.ShowError("数据库初始化失败，程序无法继续运行。\r\n\r\n"
                                        + "数据库路径：" + ConfigHelper.DbFilePath
                                        + "\r\n\r\n详情请查看 Logs 目录下的 error 日志。");
                return;
            }

            // ---------- 4. 启动主界面 ----------
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Fatal("主界面运行期间发生未捕获异常", ex);
                MessageHelper.ShowError("程序发生严重错误，即将退出：\r\n\r\n" + ex.Message
                                        + "\r\n\r\n详情请查看 Logs 目录下的 error 日志。");
            }
            finally
            {
                LogHelper.Instance.Info("================ 程序退出 ================");
                ReleaseMutex();
            }
        }

        // ============================================================
        // 异常处理
        // ============================================================

        /// <summary>
        /// UI 线程未处理异常
        /// 【处理策略】记日志 + 提示，程序继续运行 ——
        ///   现场正在喷码时因为一个小异常就整体退出，代价太大；这里选择"报告但不退出"。
        /// </summary>
        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Exception ex = e.Exception;
            LogHelper.Instance.Error("UI 线程未处理异常", ex);

            try
            {
                MessageHelper.ShowError("操作过程中发生错误：\r\n\r\n" + ex.Message
                                        + "\r\n\r\n详细信息已记录到 Logs 目录下的 error 日志。");
            }
            catch (Exception exTip)
            {
                System.Diagnostics.Debug.WriteLine("提示异常信息失败：" + exTip.Message);
            }
        }

        /// <summary>
        /// 非 UI 线程未处理异常（如后台导入线程）
        /// 【处理策略】这是最后一道防线，能拦到的都是致命错误，记录后由运行时终止进程。
        /// </summary>
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception? ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                LogHelper.Instance.Fatal("非 UI 线程未处理异常，程序即将终止", ex);
            }
            else
            {
                LogHelper.Instance.Fatal("非 UI 线程未处理异常（无异常对象），程序即将终止");
            }

            try
            {
                MessageHelper.ShowError("程序发生严重错误，即将退出。\r\n\r\n"
                                        + "详情请查看 Logs 目录下的 error 日志。");
            }
            catch (Exception exTip)
            {
                System.Diagnostics.Debug.WriteLine("提示致命异常失败：" + exTip.Message);
            }
        }

        /// <summary>程序退出事件</summary>
        private static void Application_ApplicationExit(object sender, EventArgs e)
        {
            LogHelper.Instance.Info("收到 ApplicationExit 事件");
        }

        // ============================================================
        // 私有辅助
        // ============================================================

        /// <summary>释放单实例互斥量</summary>
        private static void ReleaseMutex()
        {
            try
            {
                if (_singleInstanceMutex != null)
                {
                    _singleInstanceMutex.ReleaseMutex();
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("释放单实例互斥量失败：" + ex.Message);
            }
        }
    }
}

using InkPrinterCode.Common;
using InkPrinterCode.DAL;

namespace InkPrinterCode
{
    /// <summary>
    /// [2026-09-10] Program entry point
    ///
    /// [This file is responsible for four things]
    ///   1. Global exception fallback: UI-thread exceptions (ThreadException) + non-UI-thread exceptions (UnhandledException)
    ///      are uniformly written to the error log and reported to the user, eliminating both "an unhandled-exception
    ///      dialog pops up and the app exits" and "the app dies silently";
    ///   2. Single-instance restriction: a named Mutex prevents the on-site operator from double-clicking repeatedly
    ///      and spawning multiple instances —— two instances writing the same SQLite database file at the same time
    ///      very easily leads to lock conflicts and data corruption;
    ///   3. Database initialization: create directory + create tables (only CREATE IF NOT EXISTS, never modify existing tables);
    ///   4. Startup / exit logging, so it is traceable "when the program started and when it closed".
    ///
    /// [Why ApplicationConfiguration.Initialize() is kept]
    ///   The .NET 8 WinForms template uses this single call in place of the old EnableVisualStyles +
    ///   SetCompatibleTextRenderingDefault + SetHighDpiMode. The two are equivalent, and hand-writing those three
    ///   lines would simply be redundant.
    /// </summary>
    internal static class Program
    {
        /// <summary>Single-instance mutex name (prefixed with Local\ to limit it to the current Windows session)</summary>
        private const string MUTEX_NAME = @"Local\InkPrinterCode_SingleInstance_Mutex";

        /// <summary>Single-instance mutex; it must be held until the program exits, otherwise it will be collected by the GC and the restriction will stop working</summary>
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

            // ---------- 1. Global exception fallback (must be hooked up first, as early as possible) ----------
            Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
            Application.ApplicationExit += new EventHandler(Application_ApplicationExit);

            // ---------- 2. Single-instance check ----------
            bool createdNew = false;
            try
            {
                _singleInstanceMutex = new Mutex(true, MUTEX_NAME, out createdNew);
            }
            catch (Exception ex)
            {
                // Failure to create the mutex does not block program startup (for example in a permission-restricted
                // environment); log it and continue
                createdNew = true;
                LogHelper.Instance.Warn("Failed to create the single-instance mutex; skipping the single-instance restriction this run: " + ex.Message);
            }

            if (!createdNew)
            {
                MessageHelper.ShowWarning("The program is already running. Do not start it again.\r\n\r\nIf you cannot see the program window, please check the taskbar or Task Manager.");
                return;
            }

            LogHelper.Instance.Info("================ Program Started ================");

            // ---------- 3. Database initialization ----------
            if (!DbInitializer.Initialize())
            {
                MessageHelper.ShowError("Database initialization failed; the program cannot continue.\r\n\r\n"
                                        + "Database path: " + ConfigHelper.DbFilePath
                                        + "\r\n\r\nPlease see the error log in the Logs directory for details.");
                return;
            }

            // ---------- 4. Start the main form ----------
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Fatal("Unhandled exception while the main form was running", ex);
                MessageHelper.ShowError("A serious error occurred in the program and it will now exit:\r\n\r\n" + ex.Message
                                        + "\r\n\r\nPlease see the error log in the Logs directory for details.");
            }
            finally
            {
                LogHelper.Instance.Info("================ Program Exited ================");
                ReleaseMutex();
            }
        }

        // ============================================================
        // Exception handling
        // ============================================================

        /// <summary>
        /// Unhandled exception on the UI thread
        /// [Handling strategy] Log it + notify the user, and let the program keep running ——
        ///   exiting the whole application because of one small exception while printing is going on on-site
        ///   is far too costly; here we choose to "report but do not exit".
        /// </summary>
        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Exception ex = e.Exception;
            LogHelper.Instance.Error("Unhandled exception on the UI thread", ex);

            try
            {
                MessageHelper.ShowError("An error occurred during the operation:\r\n\r\n" + ex.Message
                                        + "\r\n\r\nDetails have been recorded in the error log in the Logs directory.");
            }
            catch (Exception exTip)
            {
                System.Diagnostics.Debug.WriteLine("Failed to show the exception message: " + exTip.Message);
            }
        }

        /// <summary>
        /// Unhandled exception on a non-UI thread (such as the background import thread)
        /// [Handling strategy] This is the last line of defense; whatever it catches is a fatal error, so after
        /// logging it the runtime terminates the process.
        /// </summary>
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception? ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                LogHelper.Instance.Fatal("Unhandled exception on a non-UI thread; the program is about to terminate", ex);
            }
            else
            {
                LogHelper.Instance.Fatal("Unhandled exception on a non-UI thread (no exception object); the program is about to terminate");
            }

            try
            {
                MessageHelper.ShowError("A serious error occurred in the program and it is about to exit.\r\n\r\n"
                                        + "Please see the error log in the Logs directory for details.");
            }
            catch (Exception exTip)
            {
                System.Diagnostics.Debug.WriteLine("Failed to show the fatal exception message: " + exTip.Message);
            }
        }

        /// <summary>Application exit event</summary>
        private static void Application_ApplicationExit(object sender, EventArgs e)
        {
            LogHelper.Instance.Info("Received the ApplicationExit event");
        }

        // ============================================================
        // Private helpers
        // ============================================================

        /// <summary>Release the single-instance mutex</summary>
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
                System.Diagnostics.Debug.WriteLine("Failed to release the single-instance mutex: " + ex.Message);
            }
        }
    }
}

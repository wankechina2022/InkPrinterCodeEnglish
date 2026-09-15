namespace InkPrinterCode.Common
{
    /// <summary>
    /// [2026-09-10] Message prompt helper —— unifies the dialog style (title, icon, button combination) across all forms
    ///
    /// [Why unify] The development standard requires consistent UI feedback: the same kind of operation uses the same
    ///   title and icon, which avoids having "Information" in one place and "System Information" in another, and also
    ///   avoids forgetting the icon on a delete confirmation.
    ///
    /// [Usage conventions]
    ///   - Operations that change data, such as delete / update / save / import, must first call ShowConfirm so the
    ///     user can confirm before execution;
    ///   - User-facing wording describes only the symptom and the suggested action; do not throw exception stacks at
    ///     the user (stacks go into the error log).
    ///
    /// [Boundaries] All methods guard against null text and treat it as an empty string, so MessageBox never throws.
    /// </summary>
    public static class MessageHelper
    {
        /// <summary>Information prompt</summary>
        public static void ShowInfo(string? message)
        {
            MessageBox.Show(SafeText(message), "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Warning prompt (validation failure, business rule interception, etc.)</summary>
        public static void ShowWarning(string? message)
        {
            MessageBox.Show(SafeText(message), "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>Error prompt (operation failed)</summary>
        public static void ShowError(string? message)
        {
            MessageBox.Show(SafeText(message), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// Confirmation dialog
        /// </summary>
        /// <param name="message">Confirmation text; it is recommended to state clearly "what will be done and how many records are affected"</param>
        /// <returns>Returns true if the user clicks "Yes", otherwise false</returns>
        public static bool ShowConfirm(string? message)
        {
            DialogResult result = MessageBox.Show(SafeText(message), "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            return result == DialogResult.Yes;
        }

        /// <summary>
        /// Dangerous-operation confirmation dialog —— the default focus is on "No" to prevent a fast double Enter
        /// press from deleting by accident
        /// </summary>
        /// <param name="message">Confirmation text</param>
        /// <returns>Returns true if the user clicks "Yes", otherwise false</returns>
        public static bool ShowDangerConfirm(string? message)
        {
            DialogResult result = MessageBox.Show(SafeText(message), "Confirm Dangerous Operation",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            return result == DialogResult.Yes;
        }

        /// <summary>Operation-succeeded prompt, e.g. ShowSuccess("Import") displays "Import succeeded!"</summary>
        public static void ShowSuccess(string? operation = "Operation")
        {
            ShowInfo(SafeText(operation) + " succeeded!");
        }

        /// <summary>Operation-failed prompt</summary>
        public static void ShowFail(string? operation = "Operation")
        {
            ShowError(SafeText(operation) + " failed. Please check the error log in the Logs directory or contact your administrator!");
        }

        /// <summary>
        /// [2026-09-10] Prompt shown while the print service is running (shared by data-viewing operations: opening
        /// the form / querying / exporting to Excel)
        /// [Why only the wording and no status check] This class lives in the Common layer, and the layer dependency
        ///   is UI → BLL → DAL → Model; Common is referenced by every layer but must not reference BLL back, so this
        ///   class does not hold a PrintServiceBLL. Instead the caller checks IsRunning on its own service instance
        ///   and only then calls this method.
        /// [Nature] It warns but does not block: after the user dismisses it with "OK" the operation continues as usual.
        /// </summary>
        public static void ShowRunningServiceTip()
        {
            ShowWarning("The print service is currently running.\r\n\r\n"
                + "This page reads the database, which may briefly contend with code sending and code claiming and "
                + "slightly slow down the code-sending rhythm (it does not affect the correctness of codes already sent).\r\n\r\n"
                + "Click \"OK\" to continue.");
        }

        /// <summary>
        /// [2026-09-10] Prompt shown while the print service is running (for data import only —— the consequences are
        /// far more serious than for a query, so the wording is deliberately stronger)
        /// [Difference from ShowRunningServiceTip] Import is "a whole batch of data written to the database in a
        ///   single transaction", which holds the database for a long time (SQLite uses a whole-file lock), so the
        ///   code-claim and mark-as-printed operations of the code-sending thread may be blocked. The consequence is
        ///   not a "slightly slower rhythm" but "a possible stall", hence the higher prompt level and the more
        ///   explicit wording.
        /// [Nature] It warns but does not block: after the user dismisses it with "OK" the import continues as usual.
        /// </summary>
        public static void ShowRunningImportTip()
        {
            ShowWarning("The print service is currently running!\r\n\r\n"
                + "Importing writes a large amount of data to the database and will occupy the database for a long "
                + "time, so code sending and code claiming may be blocked, causing the code sending to stall.\r\n\r\n"
                + "Click \"OK\" to continue the import.");
        }

        /// <summary>
        /// Text fallback: convert null to an empty string
        /// </summary>
        private static string SafeText(string? text)
        {
            if (text == null)
            {
                return string.Empty;
            }
            return text;
        }
    }
}

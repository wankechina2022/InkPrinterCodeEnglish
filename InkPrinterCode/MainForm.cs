using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Forms;
using InkPrinterCode.Model;

namespace InkPrinterCode
{
    /// <summary>
    /// [2026-09-10] Main form
    ///
    /// [Phase 1 wiring scope]
    ///   "Data Import" -> select file -> confirm -> progress form -> background import -> result dialog -> refresh dashboard
    ///   "Data View"  -> open the data view form (query / paging / export / delete) -> refresh dashboard after close
    ///
        /// [Printing feature wiring scope]
    ///   "Start Printing" -> PrintServiceBLL.Start(): connect -> spin up thread -> signal setup / clear queues -> prefill codes -> heartbeat / send codes
    ///   "Stop Printing"  -> confirmation box -> PrintServiceBLL.Stop(): seven-step stop sequence (all threads exit, all objects released)
    ///   "Test Print"     -> PrintServiceBLL.TestPrint(): send the full AB12345 buffer frame (available only while running)
    ///   State linkage: Not Started = start available; Starting/Stopping = all locked; Running = stop + test available (mutual exclusion requirement)
    ///   txtRunLog: reverse order (newest on top), auto-truncated beyond RunLogMaxLines lines
    ///   lblPrinterStatus: Disconnected -> Running (green) -> Offline (red, heartbeat timeout)
    ///
    /// [Dashboard data sources]
    ///   Available organic codes = count of NotPrinted status (real DB query)
    ///   Codes sent (lblSendCount) = counter for the current run: +1 as soon as Write is reached, regardless of write success or machine feedback
    ///   Printed count (lblPrintedCount) = counter for the current run: only counts 0x32 returned by the machine (print complete)
    ///   Both counters are reset to zero by PrintServiceBLL on Start() (instruction of 2026-09-10 20:23)
    ///
    /// [Event binding location] All bound manually with += inside MainForm.cs, not through the designer.
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>Print service state machine (the core of the print service; events fire on background threads, handlers always marshal via Invoke)</summary>
        private readonly PrintServiceBLL _printService = new PrintServiceBLL();

        public MainForm()
        {
            InitializeComponent();
            BindEvents();
            BindPrintService();
        }

        // ============================================================
        // Event binding
        // ============================================================

        /// <summary>Bind all control events in one place</summary>
        private void BindEvents()
        {
            this.Load += new EventHandler(MainForm_Load);
            btnImportData.Click += new EventHandler(btnImportData_Click);
            btnDataView.Click += new EventHandler(btnDataView_Click);

            btnStart.Click += new EventHandler(btnStart_Click);
            btnStop.Click += new EventHandler(btnStop_Click);
            btnTestPrint.Click += new EventHandler(btnTestPrint_Click);

            // [2026-09-10] Also bind the two configuration entry buttons (feedback at 15:07:
            //   clicking did nothing -- the handlers already existed, but BindEvents missed the binding)
            btnPrintConfig.Click += new EventHandler(btnPrintConfig_Click);
            btnSystemConfig.Click += new EventHandler(btnSystemConfig_Click);

            // Close confirmation: closing the program while printing is running would interrupt production, so ask first
            this.FormClosing += new FormClosingEventHandler(MainForm_FormClosing);

            // Exit fallback: no matter where the exit comes from, the stop sequence must complete (all threads exit, all connections closed)
            this.FormClosed += new FormClosedEventHandler(MainForm_FormClosed);
        }

        /// <summary>
        /// Subscribe to print service events
        /// [Threading] All events fire on the service background thread; handlers uniformly check
        /// InvokeRequired to marshal back to the UI thread.
        /// </summary>
        private void BindPrintService()
        {
            _printService.RunLog += new Action<string>(PrintService_RunLog);
            _printService.ServiceStateChanged += new Action<string, bool>(PrintService_ServiceStateChanged);
            _printService.PrinterStateChanged += new Action<string, System.Drawing.Color>(PrintService_PrinterStateChanged);
            _printService.DashboardChanged += new Action(PrintService_DashboardChanged);
        }

        /// <summary>Form load: initialize the run log area and initial button states, refresh dashboard</summary>
        private void MainForm_Load(object sender, EventArgs e)
        {
            // Run log area: read-only + vertical scrollbar (so history stays reachable when content grows)
            txtRunLog.ReadOnly = true;
            txtRunLog.ScrollBars = ScrollBars.Vertical;
            txtRunLog.Text = string.Empty;

            // Initial button linkage: Not Started = start + test available, stop disabled
            // [2026-09-10 20:05] Instruction: test print is now fully independent (creates its own connection,
            //   closes right after sending) and is mutually exclusive with start/stop printing --
            //   it can be used in the Not Started state (previously only while running)
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            btnTestPrint.Enabled = true;

            RefreshDashboard();
        }

        // ============================================================
        // Data import
        // ============================================================

        /// <summary>
        /// "Data Import" button -- select file -> confirm -> background import -> result dialog
        /// </summary>
        private void btnImportData_Click(object sender, EventArgs e)
        {
            // [2026-09-10] Instruction: show a tip first while the printing service is running
            //   (tip only, no blocking -- after dismissing it the import continues).
            //   Deliberately placed before "select file" -- consistent with the data view page:
            //   the risk is disclosed as soon as the action starts, instead of waiting for the user
            //   to finish picking a file. Import writes the whole batch in a single transaction and
            //   holds the DB for a long time, hence the emphasized text ShowRunningImportTip()
            //   (see Common/MessageHelper.cs).
            if (_printService != null && _printService.IsRunning)
            {
                MessageHelper.ShowRunningImportTip();
            }

            string filePath = string.Empty;

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the code data file to import";
                dialog.Filter = "Code data files|*.txt;*.xls;*.xlsx|Text files (*.txt)|*.txt|Excel files|*.xls;*.xlsx";
                dialog.Multiselect = false;
                dialog.CheckFileExists = true;
                dialog.RestoreDirectory = true;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                filePath = dialog.FileName;
            }

            // Block unsupported formats at the UI layer first, so no progress dialog appears before the error
            bool supported = false;
            EnumHelper.GetSourceTypeByExtension(filePath, out supported);
            if (!supported)
            {
                MessageHelper.ShowWarning("Unsupported file format. Please select a .txt / .xls / .xlsx file.");
                return;
            }

            string fileName = Path.GetFileName(filePath);

            // [2026-09-10] Excel long-code precision reminder -- this is a limitation of Excel's own
            //   double precision, which the program cannot compensate for; the only option is to keep
            //   reminding the user before import to set the code value column to "Text" format.
            bool isExcelFile = filePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)
                            || filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

            string confirmText;
            if (isExcelFile)
            {
                confirmText = "Confirm importing the following file?\r\n\r\n" + fileName + "\r\n\r\n"
                              + "[Reminder] Before importing from Excel, make sure the code value column is set to \"Text\" format:\r\n"
                              + "If a long code over 15 digits is stored as a number, Excel itself will lose the trailing digits,\r\n"
                              + "and it cannot be restored after import -- the data can only be deleted and re-imported!\r\n\r\n"
                              + "Do not close the program during the import.";
            }
            else
            {
                confirmText = "Confirm importing the following file?\r\n\r\n" + fileName
                              + "\r\n\r\nDo not close the program during the import.";
            }

            if (!MessageHelper.ShowConfirm(confirmText))
            {
                return;
            }

            btnImportData.Enabled = false;

            try
            {
                ImportResult? result = null;

                // The progress form is created and disposed in one go (development convention: child forms must be released after ShowDialog)
                using (ImportProgressForm progressForm = new ImportProgressForm(filePath))
                {
                    progressForm.ShowDialog(this);
                    result = progressForm.Result;
                }

                if (result == null)
                {
                    MessageHelper.ShowError("The import ended abnormally without producing a result. Please check the error log.");
                    return;
                }

                if (result.Success)
                {
                    MessageHelper.ShowInfo(result.Message);
                }
                else if (result.Canceled)
                {
                    MessageHelper.ShowWarning(result.Message);
                }
                else
                {
                    MessageHelper.ShowError(result.Message);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Import flow failed", ex);
                MessageHelper.ShowError("Import failed: " + ex.Message);
            }
            finally
            {
                btnImportData.Enabled = true;
                RefreshDashboard();
            }
        }

        // ============================================================
        // Data view
        // ============================================================

        /// <summary>
        /// "Data View" button -- open the data view form
        /// Refresh the dashboard after it closes: deletions may have been performed in that form, so counts can change.
        /// </summary>
        private void btnDataView_Click(object sender, EventArgs e)
        {
            try
            {
                // [2026-09-10] Requirement: query/export on the data view page should show a tip first
                //   while the printing service is running. That form cannot obtain the service instance
                //   itself, so _printService is injected here (read-only IsRunning, no control actions).
                using (DataViewForm form = new DataViewForm(_printService))
                {
                    form.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to open the data view form", ex);
                MessageHelper.ShowError("Failed to open data view: " + ex.Message);
            }
            finally
            {
                RefreshDashboard();
            }
        }

        // ============================================================
        // Printing operations (printing flow wiring)
        // ============================================================

        /// <summary>
        /// "Start Printing" button
        /// [Flow] The state machine runs in the background: read config -> create connection -> spin up resident threads ->
        ///   print signal setup -> clear the three queues -> prefill codes (taking a code marks it Printed, occupancy model) ->
        ///   heartbeat + send loop.
        /// [Failure] A connection or prefill failure automatically returns to "Not Started"; the reason is visible in red in the run log.
        /// </summary>
        private void btnStart_Click(object sender, EventArgs e)
        {
            // Starting with no codes is allowed too (it stays running and waits for an import by design); missing config is detected by the state machine and reported back
            _printService.Start();
        }

        /// <summary>
        /// "Stop Printing" button -- confirm first, then run the seven-step stop sequence
        /// [Flow] stop heartbeat -> stop threads -> clear printer queues -> (v5: no code is rolled back) -> close connection -> release -> restore UI
        /// </summary>
        private void btnStop_Click(object sender, EventArgs e)
        {
            if (!MessageHelper.ShowConfirm("Confirm stopping printing?\r\n\r\n"
                                           + "This will disconnect from the inkjet printer and clear its internal buffer queue.\r\n"
                                           + "Codes already written to the printer remain in \"Printed\" status and will not be re-sent."))
            {
                return;
            }

            _printService.Stop();
        }

        /// <summary>
        /// "Test Print" button -- create a standalone connection -> print signal setup -> clear the three queues -> send test code AB12345 -> close connection
        /// [19:53 instruction] Fully independent of production: creates its own temporary connection and releases it right after sending;
        ///   available only in the "Not Started" state (mutually exclusive with start/stop printing); button availability is driven by ServiceStateChanged.
        /// </summary>
        private void btnTestPrint_Click(object sender, EventArgs e)
        {
            _printService.TestPrint();
        }

        // ============================================================
        // Print service events (fired on background threads, all marshalled back to the UI thread via Invoke)
        // ============================================================

        /// <summary>
        /// Run log: timestamp + content, inserted at the top of txtRunLog (reverse order as required), truncated when over the line limit
        /// </summary>
        private void PrintService_RunLog(string message)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(delegate () { AppendRunLog(message); }));
                return;
            }

            AppendRunLog(message);
        }

        /// <summary>Actually append one run log line (must be called on the UI thread)</summary>
        private void AppendRunLog(string message)
        {
            try
            {
                // [2026-09-10] Requirement: timestamps accurate to the millisecond -- to make the actual
                //   "code sent -> print complete" interval easy to distinguish
                string stamped = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
                string combined = stamped + Environment.NewLine + txtRunLog.Text;

                // Auto-truncate the tail beyond the configured line count (requirement: drop data older than 100 lines)
                int maxLines = ConfigHelper.RunLogMaxLines;
                string[] lines = combined.Split(new string[] { Environment.NewLine }, StringSplitOptions.None);

                if (lines.Length > maxLines)
                {
                    string[] trimmed = new string[maxLines];
                    Array.Copy(lines, trimmed, maxLines);
                    lines = trimmed;
                }

                txtRunLog.Text = string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to update the run log area", ex);
            }
        }

        /// <summary>
        /// Production state change: lblStatus text and color + three-button linkage (mutual exclusion requirement)
        ///   Not Started -> start available; Starting/Stopping -> all locked; Running -> stop + test available.
        /// </summary>
        private void PrintService_ServiceStateChanged(string text, bool isRunning)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(delegate () { PrintService_ServiceStateChanged(text, isRunning); }));
                return;
            }

            lblStatus.Text = text;

            if (isRunning)
            {
                lblStatus.ForeColor = System.Drawing.Color.Green;
            }
            else
            {
                lblStatus.ForeColor = System.Drawing.Color.FromArgb(0, 64, 0);
            }

            // Three-button linkage: start and test are only allowed in "Not Started" (they are mutually exclusive), stop only while running
            btnStart.Enabled = (text == "Not Started");
            btnStop.Enabled = isRunning;
            btnTestPrint.Enabled = (text == "Not Started");
        }

        /// <summary>Printer state change: Disconnected (dark green) -> Running (green) -> Offline (red, heartbeat timeout)</summary>
        private void PrintService_PrinterStateChanged(string text, System.Drawing.Color color)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(delegate () { PrintService_PrinterStateChanged(text, color); }));
                return;
            }

            lblPrinterStatus.Text = text;
            lblPrinterStatus.ForeColor = color;
        }

        /// <summary>Dashboard number change (fired after a code is sent successfully): re-query the DB and refresh</summary>
        private void PrintService_DashboardChanged()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(RefreshDashboard));
                return;
            }

            RefreshDashboard();
        }

        // ============================================================
        // Form close fallbacks
        // ============================================================

        /// <summary>
        /// Close confirmation: exiting directly while the printing service is running would interrupt production, so ask first
        /// </summary>
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_printService.IsRunning && e.CloseReason == CloseReason.UserClosing)
            {
                if (!MessageHelper.ShowConfirm("The printing service is running. Closing the program will automatically stop printing.\r\n\r\n"
                                               + "Codes already written to the printer remain in \"Printed\" status and will not be re-sent.\r\n"
                                               + "Confirm exiting the program?"))
                {
                    e.Cancel = true;
                }
            }
        }

        /// <summary>
        /// Close fallback: run the stop sequence synchronously to ensure all threads exit, all connections close
        /// and all objects are released (hard requirement)
        /// </summary>
        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                _printService.StopSync();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to stop the printing service on exit", ex);
            }

            // [2026-09-16] P2: dispose the print-service instance on form close so its AutoResetEvent handles are released
            try
            {
                _printService.Dispose();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to dispose the printing service on exit", ex);
            }
        }

        // ============================================================
        // Configuration entries (configuration UI wiring)
        // ============================================================

        /// <summary>
        /// "Printer Config" button -- open the connection configuration form (TCP / serial, save both rows)
        /// [Convention] Opened with ShowDialog, disposed with using.
        /// </summary>
        private void btnPrintConfig_Click(object sender, EventArgs e)
        {
            try
            {
                using (PrinterConfigForm form = new PrinterConfigForm())
                {
                    form.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to open the printer config form", ex);
                MessageHelper.ShowError("Failed to open printer config: " + ex.Message);
            }
        }

        /// <summary>
        /// "System Parameters" button -- open the system parameter configuration form (14 adjustable parameters, effective on save)
        /// [Convention] Opened with ShowDialog, disposed with using.
        /// </summary>
        private void btnSystemConfig_Click(object sender, EventArgs e)
        {
            try
            {
                using (SystemConfigForm form = new SystemConfigForm())
                {
                    form.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to open the system parameter config form", ex);
                MessageHelper.ShowError("Failed to open system parameters: " + ex.Message);
            }
        }

        // ============================================================
        // Dashboard refresh
        // ============================================================

        /// <summary>
        /// Refresh the production dashboard numbers
        /// [Data sources] Available organic codes:
        ///   running and a valid base is known -> base - codes sent (in-memory subtraction, zero DB queries during the run, D1 instruction of 2026-09-11);
        ///   otherwise (not started / after stop / base unknown) -> live DB query (count of NotPrinted status);
        ///   codes sent / printed count = counters for the current run, read directly from the BLL (20:23 instruction, no more cumulative DB queries).
        /// [Boundary] A DB query failure must not prevent the main form from starting -- catch it, keep the previous displayed values and only log.
        /// </summary>
        private void RefreshDashboard()
        {
            try
            {
                // [2026-09-11] Decouple the dashboard from DB queries (D1): use in-memory subtraction while
                //   running and base >= 0. Rationale: in SendOneCode a successful MarkPrinted is always
                //   accompanied by codes sent +1 (on failure neither changes), so the decrease in NotPrinted
                //   stock is identical to codes sent, and subtracting gives the true remaining count in the DB
                //   (confirmed: it does not matter whether the send succeeded).
                //   Math.Max(0, ...) is purely a safeguard against a negative display (convention: controls must
                //   never show abnormal values).
                //   Not started / after stop / base unknown (-1) -> fall back to a live DB query, original logic unchanged.
                int availableCount;
                if (_printService != null && _printService.IsRunning
                    && _printService.AvailableBase >= 0)
                {
                    availableCount = Math.Max(0,
                        _printService.AvailableBase - _printService.RunSendCount);
                }
                else
                {
                    availableCount = CodeDataDAL.GetStatusCount(PrintStatus.NotPrinted);
                }

                lblAvailableCount.Text = availableCount.ToString();

                // [2026-09-10] 20:23 instruction: the two counters below now use the "current run" scope
                //   (reset to zero on Start); accumulation is the BLL's responsibility, this code only reads
                //   them for display:
                //   - Codes sent  = +1 as soon as Write is reached (regardless of write success, no machine feedback)
                //   - Printed count = only counts 0x32 returned by the machine
                //   Their difference is the in-flight amount ("sent but not yet printed by the machine").
                lblSendCount.Text = _printService.RunSendCount.ToString();
                lblPrintedCount.Text = _printService.RunPrintedCount.ToString();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to refresh the production dashboard", ex);
            }
        }

      
    }
}

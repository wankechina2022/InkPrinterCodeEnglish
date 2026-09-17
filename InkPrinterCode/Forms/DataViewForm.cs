using System.Data;
using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] Data view form -- code data query / paging / export to Excel / conditional batch delete
    ///
    /// [Development conventions applied]
    ///   1. Child forms are always opened with ShowDialog and disposed by the caller (wrapped in using);
    ///   2. Write operations (delete) must show a confirmation box first, displaying the exact number of affected rows;
    ///   3. The running button is disabled first to avoid duplicate submissions from repeated clicks;
    ///   4. All exceptions are logged as error; the UI only shows friendly text, never a stack trace.
    ///
    /// [Filter scope (confirmed)] Only "print status" + "time range" are kept:
    ///   no batch filter and no fuzzy code value search. The time range filters on "import time" (CreateTime).
    ///
    /// [Time range semantics] The date picker only lets the user choose a day;
    ///   the start time is automatically set to 00:00:00 and the end time to 23:59:59, so picking the same
    ///   day filters the whole day of data.
    ///
    /// [Delete constraints (adjusted 2026-09-10, two paths)]
    ///   Default path: codes in NotPrinted (PrintStatus=0) are never deleted --
    ///     the confirmation box shows "N deletable, M NotPrinted skipped"; even if the UI check is wrong,
    ///     the DAL DELETE statement still hard-codes PrintStatus &lt;&gt; 0 as a fallback.
    ///   Forced cleanup path: with "include NotPrinted" checked, NotPrinted data is deleted as well --
    ///     used only to clean up dirty data from a bad import (typically: the Excel code value column was not
    ///     set to Text format, so long codes lost their trailing digits). A second strong confirmation box is
    ///     required, and the result text explicitly states "include NotPrinted".
    ///
    /// [Origin of the red reminder at the top] An Excel numeric cell is internally a double, which can only
    ///   represent 15~16 digit integers exactly. If the code value column is stored as "Number" format, a long
    ///   code over 15 digits loses its trailing digits inside Excel itself, so the program already reads a wrong
    ///   value and it cannot be recovered -- the only prevention is setting the column to "Text" before import.
    ///   Hence this reminder is kept permanently on the UI instead of being buried in documentation.
    /// </summary>
    public class DataViewForm : Form
    {
        // ============================================================
        // Fields
        // ============================================================

        private int _pageIndex = 1;
        private int _pageSize = 100;
        private int _totalCount = 0;
        private int _totalPages = 0;

        private ComboBox cboStatus = new ComboBox();
        private CheckBox chkTime = new CheckBox();
        private DateTimePicker dtpStart = new DateTimePicker();
        private DateTimePicker dtpEnd = new DateTimePicker();
        private CheckBox chkIncludeNotPrinted = new CheckBox();
        private Button btnQuery = new Button();
        private Button btnExport = new Button();
        private Button btnDelete = new Button();
        private DataGridView dgvData = new DataGridView();
        private Button btnFirst = new Button();
        private Button btnPrev = new Button();
        private Button btnNext = new Button();
        private Button btnLast = new Button();
        private Label lblPageInfo = new Label();
        private Label lblTotal = new Label();
        private Panel pnlTip;
        private Label lblTip;
        private Panel pnlTop;
        private Label lblStatusTitle;
        private Label lblTo;
        private Panel pnlBottom;

        /// <summary>
        /// [2026-09-10] Print service reference (injected by the caller) -- this form only reads IsRunning from it, it performs no control actions.
        /// [Boundary] null is allowed (e.g. unit tests or another entry point constructing it directly),
        ///   and every usage site must null-check first -- see ShowRunningServiceTipIfRunning().
        /// </summary>
        private PrintServiceBLL? _printService = null;

        // ============================================================
        // Construction and layout
        // ============================================================

        /// <summary>
        /// Constructor
        /// [2026-09-10] Requirement: when this form is opened, and when "Query" or "Export Excel" is clicked inside it,
        ///   show a tip whenever the printing service is running (tip only, no blocking). Three check points in total,
        ///   see ShowRunningServiceTipIfRunning(). The service instance must be passed in by the caller (the main form) --
        ///   this form cannot obtain it on its own.
        /// </summary>
        /// <param name="printService">Print service instance; null is allowed, in which case no running tip is shown.</param>
        public DataViewForm(PrintServiceBLL printService)
        {
            _printService = printService;
            _pageSize = ConfigHelper.PageSize;

            InitializeComponent();
        }

        /// <summary>
        /// Running tip (shared by form open / query / export): only shown when the state is Running
        /// [2026-09-10] Decided semantics:
        ///   [Only check Running] The tip appears strictly in the "Running" state; the two transitional states
        ///   "Starting / Stopping" do not trigger it;
        ///   [Tip, do not block] Show an "OK" box once; after the user dismisses it execution continues as usual,
        ///   never returning early to block the operation;
        ///   [null safe] When _printService is null (caller did not inject), skip entirely and show no box.
        /// [Text source] Uniformly provided by MessageHelper.ShowRunningServiceTip() --
        ///   part of the same text system as the data import tip, so future wording changes touch only Common.
        /// [Call sites (three)] 1) opening the form in DataViewForm_Load; 2) clicking "Query"; 3) clicking "Export Excel".
        /// [Boundary] IsRunning takes the lock internally, so reading it directly here never conflicts with the state machine.
        /// </summary>
        private void ShowRunningServiceTipIfRunning()
        {
            if (_printService == null)
            {
                return;
            }

            if (!_printService.IsRunning)
            {
                return;
            }

            MessageHelper.ShowRunningServiceTip();
        }

        /// <summary>
        /// UI layout
        /// [Note] Controls are created by hand instead of using the designer -- the structure lives in a single
        /// file, so layout changes do not require switching back and forth between designer and code.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(DataViewForm));
            pnlTip = new Panel();
            lblTip = new Label();
            pnlTop = new Panel();
            lblStatusTitle = new Label();
            lblTo = new Label();
            pnlBottom = new Panel();
            pnlTip.SuspendLayout();
            pnlTop.SuspendLayout();
            SuspendLayout();
            // 
            // pnlTip
            // 
            pnlTip.BackColor = Color.FromArgb(255, 238, 238);
            pnlTip.Controls.Add(lblTip);
            pnlTip.Dock = DockStyle.Top;
            pnlTip.Location = new Point(0, 0);
            pnlTip.Name = "pnlTip";
            pnlTip.Padding = new Padding(10, 4, 10, 4);
            pnlTip.Size = new Size(1443, 46);
            pnlTip.TabIndex = 2;
            // 
            // lblTip
            // 
            lblTip.Dock = DockStyle.Fill;
            lblTip.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            lblTip.ForeColor = Color.FromArgb(192, 0, 0);
            lblTip.Location = new Point(10, 4);
            lblTip.Name = "lblTip";
            lblTip.Size = new Size(1423, 38);
            lblTip.TabIndex = 0;
            lblTip.Text = resources.GetString("lblTip.Text");
            lblTip.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // pnlTop
            // 
            pnlTop.Controls.Add(lblStatusTitle);
            pnlTop.Controls.Add(lblTo);
            pnlTop.Dock = DockStyle.Top;
            pnlTop.Location = new Point(0, 46);
            pnlTop.Name = "pnlTop";
            pnlTop.Padding = new Padding(10);
            pnlTop.Size = new Size(1443, 96);
            pnlTop.TabIndex = 1;
            // 
            // lblStatusTitle
            // 
            lblStatusTitle.Location = new Point(15, 22);
            lblStatusTitle.Name = "lblStatusTitle";
            lblStatusTitle.Size = new Size(50, 25);
            lblStatusTitle.TabIndex = 0;
            lblStatusTitle.Text = "Status:";
            // 
            // lblTo
            // 
            lblTo.Location = new Point(512, 22);
            lblTo.Name = "lblTo";
            lblTo.Size = new Size(25, 25);
            lblTo.TabIndex = 1;
            lblTo.Text = "to";
            // 
            // pnlBottom
            // 
            pnlBottom.Dock = DockStyle.Bottom;
            pnlBottom.Location = new Point(0, 801);
            pnlBottom.Name = "pnlBottom";
            pnlBottom.Size = new Size(1443, 48);
            pnlBottom.TabIndex = 0;
            // 
            // DataViewForm
            // 
            ClientSize = new Size(1443, 849);
            Controls.Add(pnlBottom);
            Controls.Add(pnlTop);
            Controls.Add(pnlTip);
            Font = new Font("Microsoft YaHei UI", 10F);
            MinimumSize = new Size(1020, 540);
            Name = "DataViewForm";
            StartPosition = FormStartPosition.CenterParent;
            Text = "Data View";
            Load += DataViewForm_Load;
            pnlTip.ResumeLayout(false);
            pnlTop.ResumeLayout(false);
            ResumeLayout(false);
        }

        // ============================================================
        // Events
        // ============================================================

        /// <summary>
        /// Form load: show the running tip first, then initialize the filter defaults and query the first page
        /// [2026-09-10] Instruction: also check once when the form opens (tip only, no blocking -- after dismissing the tip the data loads as usual).
        /// </summary>
        private void DataViewForm_Load(object sender, EventArgs e)
        {
            // [2026-09-10] Instruction: tip immediately on form open -- deliberately placed before RefreshData(),
            //   so the user is told first that "entering this page reads the DB and may contend with code
            //   fetching for printing" before the read actually happens; only then is the order semantically correct.
            ShowRunningServiceTipIfRunning();

            cboStatus.DataSource = EnumHelper.GetPrintStatusItems(true);

            DateTime today = DateTime.Now.Date;
            dtpStart.Value = today;
            dtpEnd.Value = today;

            RefreshData();
        }

        /// <summary>Enable/disable the time controls when the time filter checkbox changes</summary>
        private void chkTime_CheckedChanged(object sender, EventArgs e)
        {
            dtpStart.Enabled = chkTime.Checked;
            dtpEnd.Enabled = chkTime.Checked;
        }

        /// <summary>
        /// [2026-09-10] "Include NotPrinted" switch state change -- also turns the delete button into "Force Delete" and bolds it
        /// [Purpose] Make "currently in high-risk delete mode" obvious at a glance on the UI, so the user does not
        ///   forget it was checked and click delete by mistake.
        /// </summary>
        private void chkIncludeNotPrinted_CheckedChanged(object sender, EventArgs e)
        {
            if (chkIncludeNotPrinted.Checked)
            {
                btnDelete.Text = "Force Delete";
                btnDelete.Font = new Font(this.Font, FontStyle.Bold);
            }
            else
            {
                btnDelete.Text = "Delete";
                btnDelete.Font = new Font(this.Font, FontStyle.Regular);
            }
        }

        /// <summary>Query: return to the first page and re-query</summary>
        private void btnQuery_Click(object sender, EventArgs e)
        {
            // [2026-09-10] Requirement: show a tip first while the printing service is running (tip only, no blocking; after dismissing it the query continues).
            ShowRunningServiceTipIfRunning();

            _pageIndex = 1;
            RefreshData();
        }

        /// <summary>First page</summary>
        private void btnFirst_Click(object sender, EventArgs e)
        {
            _pageIndex = 1;
            RefreshData();
        }

        /// <summary>Previous page</summary>
        private void btnPrev_Click(object sender, EventArgs e)
        {
            if (_pageIndex > 1)
            {
                _pageIndex--;
                RefreshData();
            }
        }

        /// <summary>Next page</summary>
        private void btnNext_Click(object sender, EventArgs e)
        {
            if (_pageIndex < _totalPages)
            {
                _pageIndex++;
                RefreshData();
            }
        }

        /// <summary>Last page</summary>
        private void btnLast_Click(object sender, EventArgs e)
        {
            if (_totalPages >= 1)
            {
                _pageIndex = _totalPages;
                RefreshData();
            }
        }

        /// <summary>
        /// Export to Excel -- exports the full result set for the current filter conditions (not just the current page)
        /// </summary>
        private void btnExport_Click(object sender, EventArgs e)
        {
            if (_totalCount <= 0)
            {
                MessageHelper.ShowWarning("There is no data under the current filter conditions; nothing to export.");
                return;
            }

            // [2026-09-10] Requirement: show a tip first while the printing service is running (tip only, no blocking).
            //   Deliberately placed after the "no data" check -- clicking with no data only shows "no data",
            //   instead of popping two boxes for a single empty click.
            ShowRunningServiceTipIfRunning();

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Export to Excel";
                dialog.Filter = "Excel files|*.xlsx";
                dialog.DefaultExt = "xlsx";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = "CodeData_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx";

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                btnExport.Enabled = false;
                Cursor oldCursor = this.Cursor;
                this.Cursor = Cursors.WaitCursor;

                try
                {
                    // [2026-09-16] Switched to the streaming export (reader -> SXSSF) so large result
                    //   sets no longer load fully into memory; UI behavior and columns are unchanged.
                    int exportedRows = CodeQueryBLL.ExportToFile(BuildFilter(), dialog.FileName, "CodeData");

                    LogHelper.Instance.Info("Excel export succeeded, file=" + dialog.FileName
                                            + ", rows=" + exportedRows.ToString());
                    MessageHelper.ShowInfo("Export succeeded!\r\n\r\nTotal " + exportedRows.ToString()
                                           + " records\r\nFile location: " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    LogHelper.Instance.Error("Excel export failed: " + dialog.FileName, ex);
                    MessageHelper.ShowError("Export failed: " + ex.Message
                                            + "\r\n\r\nSee the error log in the Logs directory for details.");
                }
                finally
                {
                    this.Cursor = oldCursor;
                    btnExport.Enabled = true;
                }
            }
        }

        /// <summary>
        /// Batch delete according to the current filter conditions
        ///
        /// [Two paths]
        ///   Default path ("Include NotPrinted" unchecked): compute preview -> skip NotPrinted -> danger confirmation -> execute -> refresh on first page;
        ///   Forced path ("Include NotPrinted" checked): NotPrinted deleted as well -> stronger danger confirmation (stating consequences) -> execute -> refresh on first page.
        ///
        /// [Purpose of the forced path] All imported data is in NotPrinted status. If "NotPrinted cannot be deleted"
        ///   were the only rule, mistakenly imported data (such as Excel long codes losing trailing digits) could
        ///   never be removed and would occupy the unique index, blocking the correct data.
        ///   Hence this channel must exist, but it requires both an explicit checkbox and a strong confirmation.
        /// </summary>
        private void btnDelete_Click(object sender, EventArgs e)
        {
            bool allowNotPrinted = chkIncludeNotPrinted.Checked;

            CodeQueryFilter filter = BuildFilter();
            filter.AllowDeleteNotPrinted = allowNotPrinted;

            int filteredCount = 0;
            int deletableCount = 0;

            try
            {
                deletableCount = CodeQueryBLL.GetDeletePreview(filter, out filteredCount);
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to get the delete preview", ex);
                MessageHelper.ShowError("Cannot compute the data to delete: " + ex.Message);
                return;
            }

            if (filteredCount <= 0)
            {
                MessageHelper.ShowWarning("There is no data under the current filter conditions; nothing to delete.");
                return;
            }

            string message;

            if (allowNotPrinted)
            {
                // Forced cleanup path: deletable = all filter matches, everything is deleted (including NotPrinted)
                message = "[DANGEROUS OPERATION] Code data in [NotPrinted] status is about to be deleted!\r\n\r\n"
                          + "  Rows to delete: " + deletableCount.ToString() + " (including NotPrinted data)\r\n\r\n"
                          + "NotPrinted data is pending production material; after deletion it must be re-imported before printing can continue.\r\n"
                          + "This operation is only meant for cleaning up dirty data from a bad import (for example, the Excel code value column "
                          + "was not set to Text format and long codes over 15 digits lost their trailing digits).\r\n\r\n"
                          + "Deleted data cannot be recovered. Confirm to continue?";
            }
            else
            {
                // Default path: NotPrinted data is protected
                if (deletableCount <= 0)
                {
                    MessageHelper.ShowWarning("All " + filteredCount.ToString()
                                              + " matching rows are in NotPrinted status.\r\n\r\n"
                                              + "Codes in NotPrinted status are pending production material and are not deletable by default.\r\n\r\n"
                                              + "If this batch really is dirty data from a bad import that needs cleaning up, "
                                              + "check \"Delete NotPrinted data as well\" above before running the delete.");
                    return;
                }

                int skippedCount = filteredCount - deletableCount;

                message = "Data will be deleted according to the current filter conditions:\r\n\r\n"
                          + "  Filter matches  : " + filteredCount.ToString() + "\r\n"
                          + "  Deletable       : " + deletableCount.ToString() + "\r\n"
                          + "  NotPrinted skip : " + skippedCount.ToString() + "\r\n\r\n"
                          + "Deleted data cannot be recovered. Confirm to continue?";
            }

            if (!MessageHelper.ShowDangerConfirm(message))
            {
                LogHelper.Instance.Info("User cancelled the batch delete (mode=" + (allowNotPrinted ? "include NotPrinted" : "protect NotPrinted")
                                        + ", filter matches " + filteredCount.ToString()
                                        + ", deletable " + deletableCount.ToString() + ")");
                return;
            }

            btnDelete.Enabled = false;

            try
            {
                DeleteResult result = CodeQueryBLL.DeleteByFilter(filter);

                if (result.Success)
                {
                    MessageHelper.ShowInfo(result.Message);
                    _pageIndex = 1;
                    RefreshData();
                }
                else
                {
                    MessageHelper.ShowError(result.Message);
                }
            }
            finally
            {
                btnDelete.Enabled = true;
            }
        }

        // ============================================================
        // Private helpers
        // ============================================================

        /// <summary>Build the query object from the conditions on the UI</summary>
        private CodeQueryFilter BuildFilter()
        {
            CodeQueryFilter filter = new CodeQueryFilter();
            filter.PageIndex = _pageIndex;
            filter.PageSize = _pageSize;

            ComboItem? selectedItem = cboStatus.SelectedItem as ComboItem;
            if (selectedItem != null)
            {
                if (selectedItem.Value != EnumHelper.ALL_OPTION_VALUE)
                {
                    filter.HasStatusFilter = true;
                    filter.Status = EnumHelper.ParsePrintStatus(selectedItem.Value);
                }
            }

            if (chkTime.Checked)
            {
                filter.HasTimeFilter = true;
                // Start time is 00:00:00 of the day and end time is 23:59:59 of the day -- picking the same day filters the whole day
                filter.StartTime = dtpStart.Value.Date.ToDbTimeString();
                filter.EndTime = dtpEnd.Value.Date.AddDays(1).AddSeconds(-1).ToDbTimeString();
            }

            return filter;
        }

        /// <summary>Query and refresh the grid and paging information</summary>
        private void RefreshData()
        {
            try
            {
                int totalCount = 0;
                DataTable table = CodeQueryBLL.Query(BuildFilter(), out totalCount);

                dgvData.DataSource = table;

                _totalCount = totalCount;
                _totalPages = (_totalCount + _pageSize - 1) / _pageSize;
                if (_totalPages < 1)
                {
                    _totalPages = 1;
                }
                if (_pageIndex > _totalPages)
                {
                    _pageIndex = _totalPages;
                }

                UpdatePager();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to query code data", ex);
                MessageHelper.ShowError("Failed to query data: " + ex.Message);
            }
        }

        /// <summary>Refresh the paging bar text and button availability</summary>
        private void UpdatePager()
        {
            lblPageInfo.Text = "Page " + _pageIndex.ToString() + " / " + _totalPages.ToString();
            lblTotal.Text = _totalCount.ToString() + " records, " + _pageSize.ToString() + " per page";

            btnFirst.Enabled = _pageIndex > 1;
            btnPrev.Enabled = _pageIndex > 1;
            btnNext.Enabled = _pageIndex < _totalPages;
            btnLast.Enabled = _pageIndex < _totalPages;
        }
    }
}

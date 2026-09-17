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
        /// UI layout (designer-compatible form)
        /// [2026-09-17 fix] Opening this form in the VS designer regenerated InitializeComponent and
        ///   silently dropped everything it could not round-trip from the old hand-written version:
        ///   dgvData (never configured, never added to the form), 8 of the 10 filter-area controls,
        ///   every paging control, and all button/checkbox event wiring. The method below rebuilds
        ///   the complete original layout in designer style (field + property + Controls.Add + event
        ///   wiring), so the designer can now round-trip it without data loss.
        /// [Kept from the 2026-09-17 designer edit] ClientSize 1443x849 -- the enlarged window.
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
            ((System.ComponentModel.ISupportInitialize)dgvData).BeginInit();
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
            lblTip.AutoSize = false;
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
            pnlTop.Controls.Add(cboStatus);
            pnlTop.Controls.Add(chkTime);
            pnlTop.Controls.Add(dtpStart);
            pnlTop.Controls.Add(lblTo);
            pnlTop.Controls.Add(dtpEnd);
            pnlTop.Controls.Add(chkIncludeNotPrinted);
            pnlTop.Controls.Add(btnQuery);
            pnlTop.Controls.Add(btnExport);
            pnlTop.Controls.Add(btnDelete);
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
            // cboStatus
            //
            cboStatus.DropDownStyle = ComboBoxStyle.DropDownList;
            cboStatus.DisplayMember = "Text";
            cboStatus.Location = new Point(65, 18);
            cboStatus.Name = "cboStatus";
            cboStatus.Size = new Size(120, 28);
            cboStatus.TabIndex = 1;
            cboStatus.ValueMember = "Value";
            //
            // chkTime
            //
            chkTime.Checked = false;
            chkTime.Location = new Point(205, 20);
            chkTime.Name = "chkTime";
            chkTime.Size = new Size(110, 25);
            chkTime.TabIndex = 2;
            chkTime.Text = "Filter by time";
            chkTime.CheckedChanged += chkTime_CheckedChanged;
            //
            // dtpStart
            //
            dtpStart.CustomFormat = "yyyy-MM-dd HH:mm:ss";
            dtpStart.Enabled = false;
            dtpStart.Format = DateTimePickerFormat.Custom;
            dtpStart.Location = new Point(325, 18);
            dtpStart.Name = "dtpStart";
            dtpStart.Size = new Size(180, 28);
            dtpStart.TabIndex = 3;
            //
            // lblTo
            //
            lblTo.Location = new Point(512, 22);
            lblTo.Name = "lblTo";
            lblTo.Size = new Size(25, 25);
            lblTo.TabIndex = 4;
            lblTo.Text = "to";
            //
            // dtpEnd
            //
            dtpEnd.CustomFormat = "yyyy-MM-dd HH:mm:ss";
            dtpEnd.Enabled = false;
            dtpEnd.Format = DateTimePickerFormat.Custom;
            dtpEnd.Location = new Point(542, 18);
            dtpEnd.Name = "dtpEnd";
            dtpEnd.Size = new Size(180, 28);
            dtpEnd.TabIndex = 5;
            //
            // chkIncludeNotPrinted
            // [2026-09-10] "Include NotPrinted" switch -- unchecked by default, NotPrinted data is
            //   protected; only when checked can NotPrinted data be deleted (plus a second strong
            //   confirmation in the click handler).
            //
            chkIncludeNotPrinted.Checked = false;
            chkIncludeNotPrinted.ForeColor = Color.FromArgb(192, 0, 0);
            chkIncludeNotPrinted.Location = new Point(15, 56);
            chkIncludeNotPrinted.Name = "chkIncludeNotPrinted";
            chkIncludeNotPrinted.Size = new Size(300, 25);
            chkIncludeNotPrinted.TabIndex = 6;
            chkIncludeNotPrinted.Text = "Delete NotPrinted data as well (for cleaning up bad imports)";
            chkIncludeNotPrinted.CheckedChanged += chkIncludeNotPrinted_CheckedChanged;
            //
            // btnQuery
            //
            btnQuery.Location = new Point(735, 52);
            btnQuery.Name = "btnQuery";
            btnQuery.Size = new Size(85, 32);
            btnQuery.TabIndex = 7;
            btnQuery.Text = "Query";
            btnQuery.Click += btnQuery_Click;
            //
            // btnExport
            //
            btnExport.Location = new Point(830, 52);
            btnExport.Name = "btnExport";
            btnExport.Size = new Size(85, 32);
            btnExport.TabIndex = 8;
            btnExport.Text = "Export Excel";
            btnExport.Click += btnExport_Click;
            //
            // btnDelete
            //
            btnDelete.ForeColor = Color.Red;
            btnDelete.Location = new Point(925, 52);
            btnDelete.Name = "btnDelete";
            btnDelete.Size = new Size(75, 32);
            btnDelete.TabIndex = 9;
            btnDelete.Text = "Delete";
            btnDelete.Click += btnDelete_Click;
            //
            // pnlBottom
            //
            pnlBottom.Controls.Add(btnFirst);
            pnlBottom.Controls.Add(btnPrev);
            pnlBottom.Controls.Add(lblPageInfo);
            pnlBottom.Controls.Add(btnNext);
            pnlBottom.Controls.Add(btnLast);
            pnlBottom.Controls.Add(lblTotal);
            pnlBottom.Dock = DockStyle.Bottom;
            pnlBottom.Location = new Point(0, 801);
            pnlBottom.Name = "pnlBottom";
            pnlBottom.Size = new Size(1443, 48);
            pnlBottom.TabIndex = 0;
            //
            // btnFirst
            //
            btnFirst.Location = new Point(15, 9);
            btnFirst.Name = "btnFirst";
            btnFirst.Size = new Size(70, 30);
            btnFirst.TabIndex = 0;
            btnFirst.Text = "First";
            btnFirst.Click += btnFirst_Click;
            //
            // btnPrev
            //
            btnPrev.Location = new Point(95, 9);
            btnPrev.Name = "btnPrev";
            btnPrev.Size = new Size(70, 30);
            btnPrev.TabIndex = 1;
            btnPrev.Text = "Previous";
            btnPrev.Click += btnPrev_Click;
            //
            // lblPageInfo
            //
            lblPageInfo.Location = new Point(180, 14);
            lblPageInfo.Name = "lblPageInfo";
            lblPageInfo.Size = new Size(150, 25);
            lblPageInfo.TabIndex = 2;
            lblPageInfo.Text = "Page 1 / 1";
            //
            // btnNext
            //
            btnNext.Location = new Point(340, 9);
            btnNext.Name = "btnNext";
            btnNext.Size = new Size(70, 30);
            btnNext.TabIndex = 3;
            btnNext.Text = "Next";
            btnNext.Click += btnNext_Click;
            //
            // btnLast
            //
            btnLast.Location = new Point(420, 9);
            btnLast.Name = "btnLast";
            btnLast.Size = new Size(70, 30);
            btnLast.TabIndex = 4;
            btnLast.Text = "Last";
            btnLast.Click += btnLast_Click;
            //
            // lblTotal
            //
            lblTotal.Location = new Point(510, 14);
            lblTotal.Name = "lblTotal";
            lblTotal.Size = new Size(400, 25);
            lblTotal.TabIndex = 5;
            lblTotal.Text = "0 records";
            //
            // dgvData
            //
            // [2026-09-10] Header row height is decoupled from font/DPI: ColumnHeadersHeightSizeMode
            //   defaults to EnableResizing (row height computed from the font, ~23px at 10F -- cramped);
            //   DisableResizing + a fixed 40px pins the header height. Affects the header row only;
            //   data row height and AutoSizeColumnsMode=Fill are untouched.
            //
            dgvData.AllowUserToAddRows = false;
            dgvData.AllowUserToDeleteRows = false;
            dgvData.AllowUserToResizeRows = false;
            dgvData.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvData.BackgroundColor = Color.White;
            dgvData.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dgvData.ColumnHeadersHeight = 40;
            dgvData.Dock = DockStyle.Fill;
            dgvData.MultiSelect = false;
            dgvData.Name = "dgvData";
            dgvData.ReadOnly = true;
            dgvData.RowHeadersVisible = false;
            dgvData.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvData.TabIndex = 10;
            //
            // DataViewForm
            // Control add order: WinForms lays out Dock in reverse z-order (the last added claims
            // the edge first), so the order must be Fill area -> bottom bar -> filter bar -> tip bar
            // to keep the reminder at the very top.
            //
            ClientSize = new Size(1443, 849);
            Controls.Add(dgvData);
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
            ((System.ComponentModel.ISupportInitialize)dgvData).EndInit();
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

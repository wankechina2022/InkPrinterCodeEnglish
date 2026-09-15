using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] System parameter configuration form (13 visible adjustable parameters + 1 hidden reserved one, with input validation)
    ///
    /// [UI structure] Two GroupBoxes:
    ///   Printing communication group: heartbeat interval / command response timeout / heartbeat timeout / reconnect interval / prefill cache count;
    ///   (the 700 reset switch was hidden and retired on 2026-09-11 by instruction; saving always writes false, see the gbxComm section comments);
    ///   Data & logging group: page size / log retention days / duplicate log limit / run log lines / master logging switch / minimum log level / dedup threshold.
    ///
    /// [Why "Max send retry count" is not visible on the UI (instruction of 2026-09-10: hide dead parameters)]
    ///   Nothing in the project reads it (the send policy is already "a failed write is marked failed, never re-sent"),
    ///   so it is a reserved parameter and is not displayed; but it must still take part in "load display + save",
    ///   otherwise SystemConfigBLL.ValidateInt would report "cannot be empty" for the missing key and abort the save.
    ///   Therefore the txtMaxRetry field, the LoadItems display and the btnSave_Click submission are all kept,
    ///   just not editable by the user.
    ///
    /// [Input validation (requirement)] Each item has a hard range check (run item by item by BLL.Validate),
    ///   plus a cross check: the heartbeat timeout must be smaller than the heartbeat interval; any violation refuses
    ///   the whole batch and highlights it in red.
    ///
    /// [Effective on save] After BLL.Save succeeds it refreshes the ConfigHelper cache and resets the LogHelper log
    ///   level cache, so all parameters take effect immediately without a restart (the heartbeat interval applies from
    ///   the next cycle; the rest apply on the next transaction / next log write).
    ///
    /// [Convention] Opened with ShowDialog and disposed after use; confirmation box before saving, buttons disabled while running.
    /// </summary>
    public class SystemConfigForm : Form
    {
        // ---------- Printing communication group ----------
        private TextBox txtHeartbeatInterval = new TextBox();
        private TextBox txtSendTimeout = new TextBox();
        private TextBox txtHeartbeatTimeout = new TextBox();
        private TextBox txtReconnectInterval = new TextBox();
        private TextBox txtCacheCount = new TextBox();
        private TextBox txtMaxRetry = new TextBox();
        private CheckBox chkTcpReset = new CheckBox();

        // ---------- Data & logging group ----------
        private TextBox txtPageSize = new TextBox();
        private TextBox txtLogKeepDays = new TextBox();
        private TextBox txtDupLogLimit = new TextBox();
        private TextBox txtRunLogLines = new TextBox();
        private CheckBox chkEnableLogging = new CheckBox();
        private ComboBox cboMinLogLevel = new ComboBox();
        private TextBox txtDedupThreshold = new TextBox();

        // ---------- Buttons ----------
        private Button btnSave = new Button();
        private Button btnReset = new Button();
        private Button btnCancel = new Button();
        private Label lblError = new Label();

        /// <summary>Load parameters and populate on construction; on load failure show a tip but the form stays usable (showing built-in defaults)</summary>
        public SystemConfigForm()
        {
            InitializeComponent();
            LoadItems();
        }

        // ============================================================
        // Layout
        // ============================================================

        private void InitializeComponent()
        {
            this.Text = "System Parameters";
            this.StartPosition = FormStartPosition.CenterParent;
            // [2026-09-10] The form and both group boxes are each widened by 30px: the new hint text
            //   "500~60000, only effective on start/reconnect/test" is about 161px, which the original 140px-wide
            //   hint box would clip; a pure coordinate adjustment with no logic impact.
            // [2026-09-10] Feedback: the bottom buttons were clipped by the form's lower border (client area is
            //   around 600~605px, the three buttons at Location.Y=570 with height 36 -> bottom edge 606px, a few
            //   pixels cut off). Outer height 640 -> 670 (+30), client area about 630~635px, leaving about 25~30px
            //   of margin below the buttons. Width and all control coordinates stay untouched.
            this.Size = new Size(500, 670);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Microsoft YaHei UI", 10F);

            int labelX = 20;
            int inputX = 150;
            int inputW = 130;
            int tipX = 295;

            // ---------- Printing communication group ----------
            GroupBox gbxComm = new GroupBox();
            gbxComm.Text = "Printing Communication Parameters";
            gbxComm.Location = new Point(15, 12);
            gbxComm.Size = new Size(455, 250);

            int row = 30;
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "Heartbeat interval (ms)", txtHeartbeatInterval, "500~600000");
            // [2026-09-10] Instruction: this item was renamed from "send ack timeout" to "command response timeout" --
            //   it only covers the protocol commands for start/reconnect (print signal setup, queue clearing) and the
            //   ack wait for test printing; production code sending uses the code constant SEND_ACK_TIMEOUT_MS (100ms)
            //   and is not affected by this item. The DB key is decoupled from the display name, so the key
            //   (SendResponseTimeoutMs) stays unchanged.
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "Command response timeout (ms)", txtSendTimeout, "500~60000, only effective on start/reconnect/test");
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "Heartbeat timeout (ms)", txtHeartbeatTimeout, "200~30000, must be less than the heartbeat interval");
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "Reconnect interval (ms)", txtReconnectInterval, "1000~600000");
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "Prefill cache count", txtCacheCount, "1~20");
            // [2026-09-10] Instruction: hide the dead parameter -- this row no longer calls AddRow, so "max send retry
            //   count" is not displayed; row is not advanced, so the 700 reset checkbox below moves up 32px
            //   automatically with no gap left behind.
            //   Note: the txtMaxRetry field, the LoadItems display and the btnSave_Click submission are all kept,
            //   otherwise BLL validation would report "parameter must not be empty" for the missing key and the whole
            //   save would fail (see the class header comments).

            // [2026-09-11] Instruction: the 700 port pre-reset feature is retired -- the checkbox is no longer added
            //   to the UI (hidden) and row is not advanced, leaving no gap. The field declaration, the LoadItems
            //   display and the btnSave_Click submission are kept: the submission now always writes "false"
            //   (see btnSave_Click) so that key is always a disabled value in the DB.
            //   If the feature is restored later: uncomment the code below and restore the submission to read
            //   chkTcpReset.Checked.

            // ---------- Data & logging group ----------
            GroupBox gbxData = new GroupBox();
            gbxData.Text = "Data and Logging Parameters";
            gbxData.Location = new Point(15, 270);
            gbxData.Size = new Size(455, 250);

            row = 30;
            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "Page size", txtPageSize, "10~1000");
            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "Log retention days", txtLogKeepDays, "0~365, 0 = never clean up");
            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "Duplicate log limit", txtDupLogLimit, "100~5000");
            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "Run log lines", txtRunLogLines, "10~1000");

            chkEnableLogging.Name = "chkEnableLogging";
            chkEnableLogging.Text = "Enable logging (when off, only the error copy is kept)";
            chkEnableLogging.Location = new Point(labelX, row + 2);
            chkEnableLogging.Size = new Size(390, 25);
            gbxData.Controls.Add(chkEnableLogging);
            row += 30;

            Label lblLevel = new Label();
            lblLevel.Text = "Minimum log level:";
            lblLevel.Location = new Point(labelX, row + 5);
            lblLevel.Size = new Size(120, 23);

            cboMinLogLevel.Name = "cboMinLogLevel";
            cboMinLogLevel.Location = new Point(inputX, row);
            cboMinLogLevel.Size = new Size(inputW, 27);
            cboMinLogLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cboMinLogLevel.Items.AddRange(new object[] { "DEBUG", "INFO", "WARN", "ERROR", "FATAL" });

            Label lblLevelTip = new Label();
            lblLevelTip.Text = "Logs below this level are not written";
            lblLevelTip.ForeColor = Color.Gray;
            lblLevelTip.Location = new Point(tipX - 10, row + 5);
            lblLevelTip.Size = new Size(140, 23);

            gbxData.Controls.Add(lblLevel);
            gbxData.Controls.Add(cboMinLogLevel);
            gbxData.Controls.Add(lblLevelTip);
            row += 32;

            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "Dedup memory threshold", txtDedupThreshold, "10000~100000000");

            // ---------- Error hint ----------
            lblError.Name = "lblError";
            lblError.ForeColor = Color.Red;
            lblError.Location = new Point(20, 528);
            lblError.Size = new Size(420, 40);

            // ---------- Buttons ----------
            btnSave.Name = "btnSave";
            btnSave.Text = "Save";
            btnSave.Location = new Point(60, 570);
            btnSave.Size = new Size(90, 36);
            btnSave.Click += new EventHandler(btnSave_Click);

            btnReset.Name = "btnReset";
            btnReset.Text = "Restore Defaults";
            btnReset.Location = new Point(180, 570);
            btnReset.Size = new Size(110, 36);
            btnReset.Click += new EventHandler(btnReset_Click);

            btnCancel.Name = "btnCancel";
            btnCancel.Text = "Cancel";
            btnCancel.Location = new Point(320, 570);
            btnCancel.Size = new Size(90, 36);
            btnCancel.Click += new EventHandler(btnCancel_Click);

            this.Controls.Add(gbxComm);
            this.Controls.Add(gbxData);
            this.Controls.Add(lblError);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnReset);
            this.Controls.Add(btnCancel);
        }

        /// <summary>Add a row to a group box: label + input + gray range hint</summary>
        private void AddRow(GroupBox owner, ref int row, int labelX, int inputX, int inputW, int tipX,
            string labelText, TextBox input, string tipText)
        {
            Label label = new Label();
            label.Text = labelText + "：";
            label.Location = new Point(labelX, row + 5);
            label.Size = new Size(125, 23);

            input.Name = "txt_" + labelText;
            input.Location = new Point(inputX, row);
            input.Size = new Size(inputW, 27);

            Label tip = new Label();
            tip.Text = tipText;
            tip.ForeColor = Color.Gray;
            tip.Location = new Point(tipX - 10, row + 5);
            // [2026-09-10] The hint box is widened from 140 to 170 to fit the long hint
            //   "500~60000, only effective on start/reconnect/test".
            tip.Size = new Size(170, 23);

            owner.Controls.Add(label);
            owner.Controls.Add(input);
            owner.Controls.Add(tip);

            row += 32;
        }

        // ============================================================
        // Load and display
        // ============================================================

        /// <summary>
        /// Load parameters and populate (on DB read failure LoadItems falls back to the built-in defaults, the form stays usable)
        /// </summary>
        private void LoadItems()
        {
            SystemConfigItem[] items = SystemConfigBLL.LoadItems().ToArray();

            Dictionary<string, string> map = new Dictionary<string, string>();
            for (int i = 0; i < items.Length; i++)
            {
                map[items[i].ConfigKey] = items[i].ConfigValue;
            }

            txtHeartbeatInterval.Text = GetVal(map, ConfigHelper.KEY_HEARTBEAT_INTERVAL_MS);
            txtSendTimeout.Text = GetVal(map, ConfigHelper.KEY_SEND_RESPONSE_TIMEOUT_MS);
            txtHeartbeatTimeout.Text = GetVal(map, ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS);
            txtReconnectInterval.Text = GetVal(map, ConfigHelper.KEY_RECONNECT_INTERVAL_MS);
            txtCacheCount.Text = GetVal(map, ConfigHelper.KEY_INITIAL_CACHE_COUNT);
            txtMaxRetry.Text = GetVal(map, ConfigHelper.KEY_MAX_RETRY_COUNT);
            chkTcpReset.Checked = GetVal(map, ConfigHelper.KEY_TCP_RESET_ENABLED).ToLowerInvariant() == "true";

            txtPageSize.Text = GetVal(map, ConfigHelper.KEY_PAGE_SIZE);
            txtLogKeepDays.Text = GetVal(map, ConfigHelper.KEY_LOG_KEEP_DAYS);
            txtDupLogLimit.Text = GetVal(map, ConfigHelper.KEY_DUPLICATE_LOG_LIMIT);
            txtRunLogLines.Text = GetVal(map, ConfigHelper.KEY_RUN_LOG_MAX_LINES);
            chkEnableLogging.Checked = GetVal(map, ConfigHelper.KEY_ENABLE_LOGGING).ToLowerInvariant() == "true";

            string level = GetVal(map, ConfigHelper.KEY_MIN_LOG_LEVEL).ToUpperInvariant();
            if (cboMinLogLevel.Items.Contains(level))
            {
                cboMinLogLevel.SelectedItem = level;
            }
            else
            {
                cboMinLogLevel.SelectedItem = "INFO";
            }

            txtDedupThreshold.Text = GetVal(map, ConfigHelper.KEY_DEDUP_HASHSET_THRESHOLD);
        }

        /// <summary>Get a value from the key/value map (returns an empty string when the key is missing)</summary>
        private static string GetVal(Dictionary<string, string> map, string key)
        {
            string value = string.Empty;
            if (map.TryGetValue(key, out value))
            {
                return value;
            }
            return string.Empty;
        }

        // ============================================================
        // Save
        // ============================================================

        /// <summary>
        /// Save: collect input -> confirmation box -> BLL validation + DB write + cache refresh -> close on success
        /// [Buttons disabled while running] (convention: save-type operations disable the buttons while running to prevent repeated clicks).
        /// </summary>
        private void btnSave_Click(object sender, EventArgs e)
        {
            lblError.Text = string.Empty;

            // ---------- Collect input (key -> raw text; validation is done uniformly by the BLL) ----------
            Dictionary<string, string> inputs = new Dictionary<string, string>();
            inputs[ConfigHelper.KEY_HEARTBEAT_INTERVAL_MS] = txtHeartbeatInterval.Text;
            inputs[ConfigHelper.KEY_SEND_RESPONSE_TIMEOUT_MS] = txtSendTimeout.Text;
            inputs[ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS] = txtHeartbeatTimeout.Text;
            inputs[ConfigHelper.KEY_RECONNECT_INTERVAL_MS] = txtReconnectInterval.Text;
            inputs[ConfigHelper.KEY_INITIAL_CACHE_COUNT] = txtCacheCount.Text;
            inputs[ConfigHelper.KEY_MAX_RETRY_COUNT] = txtMaxRetry.Text;
            // [2026-09-11] Instruction: the 700 pre-reset is retired -- the UI is hidden, so this always writes
            //   "false" (the unchecked value) and no longer reads chkTcpReset.Checked, ensuring that key is always
            //   disabled in the DB after saving.
            inputs[ConfigHelper.KEY_TCP_RESET_ENABLED] = "false";
            inputs[ConfigHelper.KEY_PAGE_SIZE] = txtPageSize.Text;
            inputs[ConfigHelper.KEY_LOG_KEEP_DAYS] = txtLogKeepDays.Text;
            inputs[ConfigHelper.KEY_DUPLICATE_LOG_LIMIT] = txtDupLogLimit.Text;
            inputs[ConfigHelper.KEY_RUN_LOG_MAX_LINES] = txtRunLogLines.Text;
            inputs[ConfigHelper.KEY_ENABLE_LOGGING] = chkEnableLogging.Checked ? "true" : "false";
            inputs[ConfigHelper.KEY_MIN_LOG_LEVEL] = cboMinLogLevel.SelectedItem == null ? string.Empty : cboMinLogLevel.SelectedItem.ToString() ?? string.Empty;
            inputs[ConfigHelper.KEY_DEDUP_HASHSET_THRESHOLD] = txtDedupThreshold.Text;

            // ---------- Confirmation box (confirm write operations first, per convention) ----------
            if (!MessageHelper.ShowConfirm("Confirm saving the system parameters?\r\n\r\nAfter saving, the parameters take effect immediately without restarting the program."))
            {
                return;
            }

            btnSave.Enabled = false;
            btnReset.Enabled = false;
            btnCancel.Enabled = false;

            try
            {
                SystemConfigBLL.ValidateResult result = SystemConfigBLL.Save(inputs);

                if (result.Success)
                {
                    MessageHelper.ShowInfo("System parameters saved successfully and are now in effect.");
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                else
                {
                    lblError.Text = result.Message;
                }
            }
            finally
            {
                btnSave.Enabled = true;
                btnReset.Enabled = true;
                btnCancel.Enabled = true;
            }
        }

        /// <summary>
        /// Restore defaults: strong confirmation -> BLL reset (DB write + cache refresh + log level refresh) -> reload the display
        /// </summary>
        private void btnReset_Click(object sender, EventArgs e)
        {
            lblError.Text = string.Empty;

            if (!MessageHelper.ShowDangerConfirm("All system parameters are about to be restored to their default values.\r\n\r\n"
                                                 + "Your current custom parameters will be overwritten. Confirm to continue?"))
            {
                return;
            }

            btnReset.Enabled = false;
            btnSave.Enabled = false;

            try
            {
                string message = string.Empty;
                if (SystemConfigBLL.ResetToDefaults(out message))
                {
                    MessageHelper.ShowInfo("All default parameters have been restored.");
                    LoadItems();
                }
                else
                {
                    lblError.Text = "Failed to restore the default values: " + message;
                }
            }
            finally
            {
                btnReset.Enabled = true;
                btnSave.Enabled = true;
            }
        }

        /// <summary>Cancel: close the form without writing to the DB</summary>
        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }
    }
}

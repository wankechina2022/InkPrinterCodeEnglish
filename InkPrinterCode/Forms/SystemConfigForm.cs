using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] System parameter configuration form (visible adjustable parameters plus reserved
    /// ones that are kept in the database but hidden from the UI, all with input validation)
    ///
    /// [UI structure] Two GroupBoxes, 11 visible items:
    ///   Printing communication group: command response timeout / reconnect interval / prefill cache count;
    ///   Data &amp; logging group: page size / log retention days / duplicate log limit / run log lines /
    ///   master logging switch / minimum log level / dedup threshold.
    ///
    /// [Hidden parameters (instruction: hide dead parameters)] Four database keys are deliberately not
    /// displayed -- every one of them still has its field, its LoadItems display and its btnSave_Click
    /// submission, because SystemConfigBLL.ValidateInt reports "cannot be empty" for a missing key and
    /// would abort the whole save, and the cross rule (heartbeat timeout &lt; heartbeat interval) needs
    /// both of its inputs:
    ///   - "Heartbeat interval (ms)" and "Heartbeat timeout (ms)" -- hidden 2026-09-17. The heartbeat
    ///     thread was disabled on 2026-09-12 (HeartbeatLoop is kept but never started; the 200ms code
    ///     send is the liveness probe now), so no running code reads either key any more.
    ///   - "Max send retry count" -- hidden 2026-09-10. Nothing in the project reads it (the send policy
    ///     is already "a failed write is marked failed, never re-sent"), so it stays a reserved parameter.
    ///   - "700 port pre-reset" -- hidden 2026-09-11. The feature is retired and saving always writes false.
    ///
    /// [Input validation (requirement)] Each item has a hard range check (run item by item by BLL.Validate),
    ///   plus a cross check: the heartbeat timeout must be smaller than the heartbeat interval; any violation
    ///   refuses the whole batch and highlights it in red.
    ///
    /// [Effective on save] After BLL.Save succeeds it refreshes the ConfigHelper cache and resets the
    ///   LogHelper log level cache, so all parameters take effect immediately without a restart (the rest
    ///   apply on the next transaction / next log write).
    ///
    /// [Layout] Every column is measured from the actual captions and hints while the form is constructed;
    ///   see the layout constants below for why hard-coded pixel columns could not work (2026-09-17).
    ///
    /// [Convention] Opened with ShowDialog and disposed after use; confirmation box before saving, buttons
    ///   disabled while running.
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

        // [2026-09-17] Layout rework: every column is measured from the real text, nothing is hard-coded.
        //
        // [What was wrong] The old layout used fixed columns (caption x=20, input x=150, hint x=295) with
        //   125px caption labels and 170px hint labels, while Label / CheckBox default to AutoSize = true --
        //   which IGNORES the Size assigned in code and draws the text at its natural width, straight over
        //   the next control or past the container border. On screen that showed up as
        //     "Command response timeout (ms)" -> "Command response timeout"
        //     "500~60000, only effective on start/reconnect/test" -> "500~60000,"
        //     "0~365, 0 = never clean up" -> "0~365, 0 =",  "Logs below this level ..." -> "Logs below"
        //     "10000~100000000" -> "10000~100000",  "Restore Defaults" -> "Restore"
        //   i.e. text was both overlapped by the neighbouring control and clipped by its container.
        //
        // [What it does now] The widest caption and the widest range hint are measured with TextRenderer --
        //   the same GDI renderer Label / CheckBox use, since UseCompatibleTextRendering stays false -- plus
        //   a small slack. The caption column, the hint column, the group boxes, the button row and the
        //   form's client area are all derived from that result, and every label is AutoSize = false with an
        //   explicit size. No caption can wrap away or push into its neighbour, and the window is exactly as
        //   wide as its widest row needs -- at any font size and any DPI.
        private const int ROW_HEIGHT = 44;          // vertical pitch of one parameter row
                                                    //   [2026-09-17] Raised from 34 to 44 on request (rows were
                                                    //   too tight). The extra 10px goes into the gap between
                                                    //   rows; the form height follows automatically through
                                                    //   clientHeight below, so the window grows with it.
        private const int TEXT_HEIGHT = 25;         // caption / hint label height
        private const int INPUT_HEIGHT = 28;        // text box height
        // [2026-09-17] Caption / input are centred inside the (taller) row band instead of being pinned to
        //   its top edge, so the extra pitch turns into an even margin above and below every row.
        private const int LABEL_OFFSET = (ROW_HEIGHT - TEXT_HEIGHT) / 2;   // caption / hint y inside a row
        private const int INPUT_OFFSET = (ROW_HEIGHT - INPUT_HEIGHT) / 2;  // text box y inside a row
        private const int BUTTON_HEIGHT = 38;
        private const int ERROR_HEIGHT = 46;
        private const int GROUP_TITLE_HEIGHT = 30;  // first row offset below the group box title
        private const int GROUP_BOTTOM_PAD = 12;    // breathing space underneath the last row
        private const int GROUP_LEFT = 15;          // group box x, inside the form
        private const int GROUP_INNER_LEFT = 16;    // caption x, inside the group box
        private const int GROUP_INNER_RIGHT_PAD = 16;
        private const int INPUT_WIDTH = 170;
        private const int GAP_LABEL_INPUT = 12;
        private const int GAP_INPUT_HINT = 16;
        private const int GAP_GROUP = 14;
        private const int GAP_BUTTON = 14;
        private const int FORM_TOP_PAD = 12;
        private const int FORM_BOTTOM_PAD = 18;
        private const int TEXT_SLACK = 8;           // measured text width -> control width
        private const int CHECK_BOX_TEXT_GAP = 24;  // [2026-09-17] now only a floor for the group width when
                                                    //   the logging switch caption is the longest text -- the
                                                    //   switch control itself spans the whole row (see below)
        private const int MEASURE_MAX_WIDTH = 4096; // upper bound handed to TextRenderer (see MeasureTextWidth)

        /// <summary>One parameter row: caption / input control / gray range hint</summary>
        private sealed class ParamRow
        {
            public ParamRow(string caption, TextBox input, string hint)
            {
                Caption = caption;
                Input = input;
                Hint = hint;
            }

            public string Caption { get; }
            public TextBox Input { get; }
            public string Hint { get; }
        }

        /// <summary>
        /// Measure a single-line caption with the renderer the labels actually use, so the result reflects
        /// real GDI glyph widths instead of a per-character estimate.
        /// </summary>
        private static int MeasureTextWidth(string text, Font font)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            Size measured = TextRenderer.MeasureText(
                text,
                font,
                new Size(MEASURE_MAX_WIDTH, TEXT_HEIGHT),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            return measured.Width;
        }

        /// <summary>Flatten several row sets so the column widths can be measured in a single pass</summary>
        private static IEnumerable<ParamRow> EnumerateRows(params ParamRow[][] sets)
        {
            foreach (ParamRow[] set in sets)
            {
                foreach (ParamRow row in set)
                {
                    yield return row;
                }
            }
        }

        private void InitializeComponent()
        {
            this.Text = "System Parameters";
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Microsoft YaHei UI", 10F);

            // ---------- Row definitions: caption / input control / gray range hint ----------
            // [2026-09-10] "Max send retry count" is deliberately absent: nothing in the project reads it
            //   (the send policy is already "a failed write is marked failed, never re-sent"), so it stays
            //   a reserved parameter and is not displayed. The txtMaxRetry field, the LoadItems display and
            //   the btnSave_Click submission are all kept, otherwise BLL validation would report "cannot be
            //   empty" for the missing key and the whole save would be refused (see the class header).
            // [2026-09-11] "700 port pre-reset" is absent for the same reason: the feature is retired, the
            //   checkbox is not added to the UI at all and btnSave_Click always writes "false", so that key
            //   stays disabled in the database. To restore the feature later, uncomment the chkTcpReset block
            //   below and make the submission read chkTcpReset.Checked again.
            // [2026-09-17] "Heartbeat interval" and "Heartbeat timeout" join the hidden list: the heartbeat
            //   thread was disabled on 2026-09-12 (HeartbeatLoop is kept but never started -- the 200ms code
            //   send is the liveness probe now), so no running code reads either key. Showing them would only
            //   invite tuning a parameter that has no effect. As with MaxRetryCount the txtHeartbeatInterval /
            //   txtHeartbeatTimeout fields, their LoadItems display and their btnSave_Click submission all
            //   stay, so both keys are still round-tripped and the BLL cross rule "heartbeat timeout less than
            //   heartbeat interval" (SystemConfigBLL.Validate) keeps receiving valid values.
            ParamRow[] commRows = new ParamRow[]
            {
                // [2026-09-10] Renamed from "send ack timeout" to "command response timeout": this item only
                //   covers the protocol commands for start/reconnect (print signal setup, queue clearing) and
                //   the ack wait for test printing; production code sending uses the code constant
                //   SEND_ACK_TIMEOUT_MS (2000ms, see PrintServiceBLL) and is not affected by it. The DB key is
                //   decoupled from the display name, so the key (SendResponseTimeoutMs) stays unchanged.
                new ParamRow("Command response timeout (ms)", txtSendTimeout, "500~60000, only effective on start/reconnect/test"),
                new ParamRow("Reconnect interval (ms)", txtReconnectInterval, "1000~600000"),
                new ParamRow("Prefill cache count", txtCacheCount, "1~20"),
            };

            ParamRow[] dataRows = new ParamRow[]
            {
                new ParamRow("Page size", txtPageSize, "10~1000"),
                new ParamRow("Log retention days", txtLogKeepDays, "0~365, 0 = never clean up"),
                new ParamRow("Duplicate log limit", txtDupLogLimit, "100~5000"),
                new ParamRow("Run log lines", txtRunLogLines, "10~1000"),
            };

            ParamRow[] tailRows = new ParamRow[]
            {
                new ParamRow("Dedup memory threshold", txtDedupThreshold, "10000~100000000"),
            };

            // ---------- Column geometry, measured from the captions and hints above ----------
            chkEnableLogging.Text = "Enable logging (when off, only the error copy is kept)";

            int labelWidth = MeasureTextWidth("Minimum log level:", this.Font);
            int hintWidth = MeasureTextWidth("Logs below this level are not written", this.Font);

            foreach (ParamRow spec in EnumerateRows(commRows, dataRows, tailRows))
            {
                labelWidth = Math.Max(labelWidth, MeasureTextWidth(spec.Caption + ":", this.Font));
                hintWidth = Math.Max(hintWidth, MeasureTextWidth(spec.Hint, this.Font));
            }

            labelWidth += TEXT_SLACK;
            hintWidth += TEXT_SLACK;

            int inputX = GROUP_INNER_LEFT + labelWidth + GAP_LABEL_INPUT;
            int hintX = inputX + INPUT_WIDTH + GAP_INPUT_HINT;
            int checkBoxNeeded = MeasureTextWidth(chkEnableLogging.Text, this.Font) + CHECK_BOX_TEXT_GAP;

            int groupWidth = Math.Max(hintX + hintWidth + GROUP_INNER_RIGHT_PAD,
                                      GROUP_INNER_LEFT + checkBoxNeeded + GROUP_INNER_RIGHT_PAD);
            int clientWidth = GROUP_LEFT * 2 + groupWidth;

            // [2026-09-17] The logging switch spans the whole inner width rather than its measured text
            //   width. A CheckBox reserves its glyph plus a gap in front of the caption, and on screen that
            //   reservation turned out to be wider than CHECK_BOX_TEXT_GAP, so the measured width still
            //   clipped the tail ("...only the error copy is" instead of "...only the error copy is kept)").
            //   Giving it the full row removes the coupling to the glyph metrics entirely: the caption can
            //   no longer clip, whatever size the theme gives the check glyph. groupWidth already includes
            //   checkBoxNeeded in its max above, so the row is at least as wide as the caption needs.
            int checkBoxWidth = groupWidth - GROUP_INNER_LEFT - GROUP_INNER_RIGHT_PAD;

            // The data group is two rows taller than its text rows: one for the logging switch, one for the
            // minimum log level combo.
            int commGroupHeight = GROUP_TITLE_HEIGHT + commRows.Length * ROW_HEIGHT + GROUP_BOTTOM_PAD;
            int dataGroupHeight = GROUP_TITLE_HEIGHT + (dataRows.Length + 2 + tailRows.Length) * ROW_HEIGHT + GROUP_BOTTOM_PAD;

            int commGroupY = FORM_TOP_PAD;
            int dataGroupY = commGroupY + commGroupHeight + GAP_GROUP;
            int errorY = dataGroupY + dataGroupHeight + 10;
            int buttonY = errorY + ERROR_HEIGHT + 10;
            int clientHeight = buttonY + BUTTON_HEIGHT + FORM_BOTTOM_PAD;

            // ---------- Printing communication group ----------
            GroupBox gbxComm = new GroupBox();
            gbxComm.Text = "Printing Communication Parameters";
            gbxComm.Location = new Point(GROUP_LEFT, commGroupY);
            gbxComm.Size = new Size(groupWidth, commGroupHeight);

            int row = GROUP_TITLE_HEIGHT;
            foreach (ParamRow spec in commRows)
            {
                AddRow(gbxComm, ref row, spec, labelWidth, inputX, hintX, hintWidth);
            }

            // ---------- Data & logging group ----------
            GroupBox gbxData = new GroupBox();
            gbxData.Text = "Data and Logging Parameters";
            gbxData.Location = new Point(GROUP_LEFT, dataGroupY);
            gbxData.Size = new Size(groupWidth, dataGroupHeight);

            row = GROUP_TITLE_HEIGHT;
            foreach (ParamRow spec in dataRows)
            {
                AddRow(gbxData, ref row, spec, labelWidth, inputX, hintX, hintWidth);
            }

            chkEnableLogging.Name = "chkEnableLogging";
            chkEnableLogging.AutoSize = false;
            chkEnableLogging.Location = new Point(GROUP_INNER_LEFT, row + LABEL_OFFSET);
            chkEnableLogging.Size = new Size(checkBoxWidth, TEXT_HEIGHT);
            chkEnableLogging.TextAlign = ContentAlignment.MiddleLeft;
            gbxData.Controls.Add(chkEnableLogging);
            row += ROW_HEIGHT;

            Label lblLevel = new Label();
            lblLevel.Text = "Minimum log level:";
            lblLevel.AutoSize = false;
            lblLevel.Location = new Point(GROUP_INNER_LEFT, row + LABEL_OFFSET);
            lblLevel.Size = new Size(labelWidth, TEXT_HEIGHT);
            lblLevel.TextAlign = ContentAlignment.MiddleLeft;

            cboMinLogLevel.Name = "cboMinLogLevel";
            cboMinLogLevel.Location = new Point(inputX, row + INPUT_OFFSET);
            cboMinLogLevel.Size = new Size(INPUT_WIDTH, INPUT_HEIGHT);
            cboMinLogLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cboMinLogLevel.Items.AddRange(new object[] { "DEBUG", "INFO", "WARN", "ERROR", "FATAL" });

            Label lblLevelTip = new Label();
            lblLevelTip.Text = "Logs below this level are not written";
            lblLevelTip.ForeColor = Color.Gray;
            lblLevelTip.AutoSize = false;
            lblLevelTip.Location = new Point(hintX, row + LABEL_OFFSET);
            lblLevelTip.Size = new Size(hintWidth, TEXT_HEIGHT);
            lblLevelTip.TextAlign = ContentAlignment.MiddleLeft;

            gbxData.Controls.Add(lblLevel);
            gbxData.Controls.Add(cboMinLogLevel);
            gbxData.Controls.Add(lblLevelTip);
            row += ROW_HEIGHT;

            foreach (ParamRow spec in tailRows)
            {
                AddRow(gbxData, ref row, spec, labelWidth, inputX, hintX, hintWidth);
            }

            // ---------- Error hint ----------
            lblError.Name = "lblError";
            lblError.ForeColor = Color.Red;
            lblError.AutoSize = false;
            lblError.Location = new Point(GROUP_LEFT + GROUP_INNER_LEFT, errorY);
            lblError.Size = new Size(clientWidth - 2 * (GROUP_LEFT + GROUP_INNER_LEFT), ERROR_HEIGHT);

            // ---------- Buttons ----------
            // [2026-09-17] Every button is sized from its own caption -- at the old fixed 110px "Restore
            //   Defaults" was drawn as "Restore" -- and the row is centred underneath the form.
            btnSave.Name = "btnSave";
            btnSave.Text = "Save";
            btnSave.Size = new Size(Math.Max(90, MeasureTextWidth(btnSave.Text, this.Font) + 44), BUTTON_HEIGHT);
            btnSave.Click += new EventHandler(btnSave_Click);

            btnReset.Name = "btnReset";
            btnReset.Text = "Restore Defaults";
            btnReset.Size = new Size(Math.Max(110, MeasureTextWidth(btnReset.Text, this.Font) + 44), BUTTON_HEIGHT);
            btnReset.Click += new EventHandler(btnReset_Click);

            btnCancel.Name = "btnCancel";
            btnCancel.Text = "Cancel";
            btnCancel.Size = new Size(Math.Max(90, MeasureTextWidth(btnCancel.Text, this.Font) + 44), BUTTON_HEIGHT);
            btnCancel.Click += new EventHandler(btnCancel_Click);

            int buttonsWidth = btnSave.Width + btnReset.Width + btnCancel.Width + 2 * GAP_BUTTON;
            int buttonX = Math.Max(GROUP_LEFT, (clientWidth - buttonsWidth) / 2);

            btnSave.Location = new Point(buttonX, buttonY);
            btnReset.Location = new Point(buttonX + btnSave.Width + GAP_BUTTON, buttonY);
            btnCancel.Location = new Point(btnReset.Location.X + btnReset.Width + GAP_BUTTON, buttonY);

            this.Controls.Add(gbxComm);
            this.Controls.Add(gbxData);
            this.Controls.Add(lblError);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnReset);
            this.Controls.Add(btnCancel);

            // [2026-09-17] The form now takes exactly the measured client size. The old hard-coded 500x670
            //   was picked before the captions were known, which is why the button row sat on the bottom
            //   border and the last hint was cut off.
            this.ClientSize = new Size(clientWidth, clientHeight);
        }

        /// <summary>
        /// Add one parameter row to a group box: caption + input + gray range hint, all using the measured
        /// column widths (see the layout constants above).
        /// </summary>
        private void AddRow(GroupBox owner, ref int row, ParamRow spec,
            int labelWidth, int inputX, int hintX, int hintWidth)
        {
            Label label = new Label();
            label.Text = spec.Caption + ":"; // [2026-09-15] plain ASCII colon for the English UI
            label.AutoSize = false;
            label.Location = new Point(GROUP_INNER_LEFT, row + LABEL_OFFSET);
            label.Size = new Size(labelWidth, TEXT_HEIGHT);
            label.TextAlign = ContentAlignment.MiddleLeft;

            spec.Input.Name = "txt_" + spec.Caption;
            spec.Input.Location = new Point(inputX, row + INPUT_OFFSET);
            spec.Input.Size = new Size(INPUT_WIDTH, INPUT_HEIGHT);

            Label tip = new Label();
            tip.Text = spec.Hint;
            tip.ForeColor = Color.Gray;
            tip.AutoSize = false;
            tip.Location = new Point(hintX, row + LABEL_OFFSET);
            tip.Size = new Size(hintWidth, TEXT_HEIGHT);
            tip.TextAlign = ContentAlignment.MiddleLeft;

            owner.Controls.Add(label);
            owner.Controls.Add(spec.Input);
            owner.Controls.Add(tip);

            row += ROW_HEIGHT;
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

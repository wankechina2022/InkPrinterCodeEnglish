using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] 系统参数配置窗体（13 项可见可调参数 + 1 项隐藏保留，带输入验证）
    ///
    /// 【界面结构】两个 GroupBox：
    ///   喷码通讯组：心跳周期 / 指令应答超时 / 心跳超时 / 重连间隔 / 预填缓存数
    ///   （700复位开关已于 2026-09-11 万总指令隐藏弃用，保存恒写 false，见 gbxComm 段注释）；
    ///   数据日志组：分页条数 / 日志保留天数 / 重复日志上限 / 运行日志行数 / 日志总开关 / 最低日志级别 / 去重阈值。
    ///
    /// 【「发送重试上限 MaxRetryCount」为什么界面看不到（2026-09-10 万总指令：死参数隐藏）】
    ///   全项目没有任何读取点（发送策略已是"写失败即标失败、绝不重发"），属保留参数，故不显示；
    ///   但它必须继续参与"加载回显 + 保存"，否则 SystemConfigBLL.ValidateInt 会因缺 key 判"不能为空"而中断保存。
    ///   因此字段 txtMaxRetry、LoadItems 回显、btnSave_Click 提交三处一律保留，只是不给用户改动。
    ///
    /// 【输入验证（万总要求）】每项都有范围硬校验（BLL.Validate 逐项执行），
    ///   附加交叉校验：心跳超时必须小于心跳周期；任何一项违规整批拒绝保存并红字定位。
    ///
    /// 【保存即生效】BLL.Save 成功后自动刷新 ConfigHelper 缓存 + 复位 LogHelper 日志级别缓存，
    ///   全部参数免重启立即生效（心跳周期下一周期生效，其余下笔事务/下次写日志生效）。
    ///
    /// 【规约】ShowDialog 打开、用完销毁；保存前确认框、执行期间禁用按钮。
    /// </summary>
    public class SystemConfigForm : Form
    {
        // ---------- 喷码通讯组 ----------
        private TextBox txtHeartbeatInterval = new TextBox();
        private TextBox txtSendTimeout = new TextBox();
        private TextBox txtHeartbeatTimeout = new TextBox();
        private TextBox txtReconnectInterval = new TextBox();
        private TextBox txtCacheCount = new TextBox();
        private TextBox txtMaxRetry = new TextBox();
        private CheckBox chkTcpReset = new CheckBox();

        // ---------- 数据日志组 ----------
        private TextBox txtPageSize = new TextBox();
        private TextBox txtLogKeepDays = new TextBox();
        private TextBox txtDupLogLimit = new TextBox();
        private TextBox txtRunLogLines = new TextBox();
        private CheckBox chkEnableLogging = new CheckBox();
        private ComboBox cboMinLogLevel = new ComboBox();
        private TextBox txtDedupThreshold = new TextBox();

        // ---------- 按钮 ----------
        private Button btnSave = new Button();
        private Button btnReset = new Button();
        private Button btnCancel = new Button();
        private Label lblError = new Label();

        /// <summary>构造时加载参数并回显；加载失败提示但窗体仍可用（显示内置默认）</summary>
        public SystemConfigForm()
        {
            InitializeComponent();
            LoadItems();
        }

        // ============================================================
        // 布局
        // ============================================================

        private void InitializeComponent()
        {
            this.Text = "系统参数配置";
            this.StartPosition = FormStartPosition.CenterParent;
            // [2026-09-10] 窗体与两个分组框各加宽 30px：新提示语「500~60000，仅启动/重连/测试生效」
            //   约 161px，原 140px 宽的提示框会把它裁掉；纯坐标调整，无逻辑影响。
            // [2026-09-10] 万总反馈：底部按钮被窗体下边框裁到（客户区高约 600~605px，
            //   三个按钮 Location.Y=570 + 高 36 → 底边 606px，差几像素被切）。
            //   外框高度 640 → 670（+30），客户区约 630~635px，按钮下方留出约 25~30px 余量。
            //   宽度与所有控件坐标一律不动。
            this.Size = new Size(500, 670);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Microsoft YaHei UI", 10F);

            int labelX = 20;
            int inputX = 150;
            int inputW = 130;
            int tipX = 295;

            // ---------- 喷码通讯组 ----------
            GroupBox gbxComm = new GroupBox();
            gbxComm.Text = "喷码通讯参数";
            gbxComm.Location = new Point(15, 12);
            gbxComm.Size = new Size(455, 250);

            int row = 30;
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "心跳周期(ms)", txtHeartbeatInterval, "500~600000");
            // [2026-09-10] 万总指令：本项改名「发码应答超时」→「指令应答超时」——它只管
            //   启动/重连的协议指令（打印信号设置、清队列）与测试喷印的应答等待；
            //   生产发码走代码常量 SEND_ACK_TIMEOUT_MS(100ms)，不受本项影响。
            //   数据库 key 与显示名解耦，key(SendResponseTimeoutMs) 保持原名不动。
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "指令应答超时(ms)", txtSendTimeout, "500~60000，仅启动/重连/测试生效");
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "心跳超时(ms)", txtHeartbeatTimeout, "200~30000，须小于心跳周期");
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "重连间隔(ms)", txtReconnectInterval, "1000~600000");
            AddRow(gbxComm, ref row, labelX, inputX, inputW, tipX, "预填缓存条数", txtCacheCount, "1~20");
            // [2026-09-10] 万总指令：死参数隐藏 —— 本行不再 AddRow，「发送重试上限」界面不显示；
            //   不推进 row，下面的 700 复位复选框自动上移 32px，不留空洞。
            //   注意：txtMaxRetry 字段、LoadItems 的回显、btnSave_Click 的提交三处继续保留，
            //   否则 BLL 校验会因缺 key 判"参数不能为空"导致整个保存失败（详见类头注释）。

            // [2026-09-11] 万总指令：700 端口预复位功能弃用 —— 复选框不再加入界面（隐藏），
            //   row 不推进，不留空洞。字段声明、LoadItems 回显、btnSave_Click 提交三处保留：
            //   提交已改为恒写 "false"（见 btnSave_Click），保证库里该 key 永远是关闭值。
            //   将来若恢复功能：解开下方注释并在提交处还原为读 chkTcpReset.Checked 即可。

            // ---------- 数据日志组 ----------
            GroupBox gbxData = new GroupBox();
            gbxData.Text = "数据与日志参数";
            gbxData.Location = new Point(15, 270);
            gbxData.Size = new Size(455, 250);

            row = 30;
            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "每页条数", txtPageSize, "10~1000");
            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "日志保留天数", txtLogKeepDays, "0~365，0=永不清理");
            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "重复日志上限", txtDupLogLimit, "100~5000");
            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "运行日志行数", txtRunLogLines, "10~1000");

            chkEnableLogging.Name = "chkEnableLogging";
            chkEnableLogging.Text = "开启日志（关闭后只保留 error 副本）";
            chkEnableLogging.Location = new Point(labelX, row + 2);
            chkEnableLogging.Size = new Size(390, 25);
            gbxData.Controls.Add(chkEnableLogging);
            row += 30;

            Label lblLevel = new Label();
            lblLevel.Text = "最低日志级别：";
            lblLevel.Location = new Point(labelX, row + 5);
            lblLevel.Size = new Size(120, 23);

            cboMinLogLevel.Name = "cboMinLogLevel";
            cboMinLogLevel.Location = new Point(inputX, row);
            cboMinLogLevel.Size = new Size(inputW, 27);
            cboMinLogLevel.DropDownStyle = ComboBoxStyle.DropDownList;
            cboMinLogLevel.Items.AddRange(new object[] { "DEBUG", "INFO", "WARN", "ERROR", "FATAL" });

            Label lblLevelTip = new Label();
            lblLevelTip.Text = "低于该级别的日志不写";
            lblLevelTip.ForeColor = Color.Gray;
            lblLevelTip.Location = new Point(tipX - 10, row + 5);
            lblLevelTip.Size = new Size(140, 23);

            gbxData.Controls.Add(lblLevel);
            gbxData.Controls.Add(cboMinLogLevel);
            gbxData.Controls.Add(lblLevelTip);
            row += 32;

            AddRow(gbxData, ref row, labelX, inputX, inputW, tipX, "去重内存阈值", txtDedupThreshold, "10000~100000000");

            // ---------- 错误提示 ----------
            lblError.Name = "lblError";
            lblError.ForeColor = Color.Red;
            lblError.Location = new Point(20, 528);
            lblError.Size = new Size(420, 40);

            // ---------- 按钮 ----------
            btnSave.Name = "btnSave";
            btnSave.Text = "保存";
            btnSave.Location = new Point(60, 570);
            btnSave.Size = new Size(90, 36);
            btnSave.Click += new EventHandler(btnSave_Click);

            btnReset.Name = "btnReset";
            btnReset.Text = "恢复默认值";
            btnReset.Location = new Point(180, 570);
            btnReset.Size = new Size(110, 36);
            btnReset.Click += new EventHandler(btnReset_Click);

            btnCancel.Name = "btnCancel";
            btnCancel.Text = "取消";
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

        /// <summary>在分组框里加一行：标签 + 输入框 + 灰色范围提示</summary>
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
            // [2026-09-10] 提示框由 140 加宽到 170：容纳「500~60000，仅启动/重连/测试生效」这句长提示。
            tip.Size = new Size(170, 23);

            owner.Controls.Add(label);
            owner.Controls.Add(input);
            owner.Controls.Add(tip);

            row += 32;
        }

        // ============================================================
        // 加载回显
        // ============================================================

        /// <summary>
        /// 加载参数并回显（读库失败时 LoadItems 自动回落内置默认，窗体仍可用）
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

        /// <summary>从键值映射取值（键不存在返回空串）</summary>
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
        // 保存
        // ============================================================

        /// <summary>
        /// 保存：收集输入 → 确认框 → BLL 校验+写库+刷缓存 → 成功关窗
        /// 【执行期间禁用按钮】（规约：保存类操作执行期间禁用按钮防连点）。
        /// </summary>
        private void btnSave_Click(object sender, EventArgs e)
        {
            lblError.Text = string.Empty;

            // ---------- 收集输入（键 → 原始文本，校验交给 BLL 统一做） ----------
            Dictionary<string, string> inputs = new Dictionary<string, string>();
            inputs[ConfigHelper.KEY_HEARTBEAT_INTERVAL_MS] = txtHeartbeatInterval.Text;
            inputs[ConfigHelper.KEY_SEND_RESPONSE_TIMEOUT_MS] = txtSendTimeout.Text;
            inputs[ConfigHelper.KEY_HEARTBEAT_TIMEOUT_MS] = txtHeartbeatTimeout.Text;
            inputs[ConfigHelper.KEY_RECONNECT_INTERVAL_MS] = txtReconnectInterval.Text;
            inputs[ConfigHelper.KEY_INITIAL_CACHE_COUNT] = txtCacheCount.Text;
            inputs[ConfigHelper.KEY_MAX_RETRY_COUNT] = txtMaxRetry.Text;
            // [2026-09-11] 万总指令：700 预复位弃用 —— UI 已隐藏，此处恒写 "false"（不勾选值），
            //   不再读 chkTcpReset.Checked，保证点保存后库里该 key 永远是关闭状态。
            inputs[ConfigHelper.KEY_TCP_RESET_ENABLED] = "false";
            inputs[ConfigHelper.KEY_PAGE_SIZE] = txtPageSize.Text;
            inputs[ConfigHelper.KEY_LOG_KEEP_DAYS] = txtLogKeepDays.Text;
            inputs[ConfigHelper.KEY_DUPLICATE_LOG_LIMIT] = txtDupLogLimit.Text;
            inputs[ConfigHelper.KEY_RUN_LOG_MAX_LINES] = txtRunLogLines.Text;
            inputs[ConfigHelper.KEY_ENABLE_LOGGING] = chkEnableLogging.Checked ? "true" : "false";
            inputs[ConfigHelper.KEY_MIN_LOG_LEVEL] = cboMinLogLevel.SelectedItem == null ? string.Empty : cboMinLogLevel.SelectedItem.ToString() ?? string.Empty;
            inputs[ConfigHelper.KEY_DEDUP_HASHSET_THRESHOLD] = txtDedupThreshold.Text;

            // ---------- 确认框（写操作先确认，规约要求） ----------
            if (!MessageHelper.ShowConfirm("确认保存系统参数？\r\n\r\n保存后参数立即生效，无需重启程序。"))
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
                    MessageHelper.ShowInfo("系统参数保存成功，已立即生效。");
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
        /// 恢复默认值：强确认 → BLL 重置（写库+刷缓存+刷日志级别）→ 回显刷新
        /// </summary>
        private void btnReset_Click(object sender, EventArgs e)
        {
            lblError.Text = string.Empty;

            if (!MessageHelper.ShowDangerConfirm("即将把全部系统参数恢复为默认值。\r\n\r\n"
                                                 + "当前自定义的参数会被覆盖，确认继续吗？"))
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
                    MessageHelper.ShowInfo("已恢复全部默认参数。");
                    LoadItems();
                }
                else
                {
                    lblError.Text = "恢复默认值失败：" + message;
                }
            }
            finally
            {
                btnReset.Enabled = true;
                btnSave.Enabled = true;
            }
        }

        /// <summary>取消：直接关窗不落库</summary>
        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }
    }
}

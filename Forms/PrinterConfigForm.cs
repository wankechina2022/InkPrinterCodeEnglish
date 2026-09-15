using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;
using InkPrinterCode.Model.Enums;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] 喷码机连接配置窗体（TCP / 串口 二选一）
    ///
    /// 【界面结构】
    ///   顶部：连接方式单选（rdoTcp / rdoSerial）—— 选中哪个，保存时哪个 IsEnabled=1；
    ///   中部：TCP 参数组（IP / 端口 / 预复位端口）+ 串口参数组（串口号下拉 / 波特率下拉）；
    ///   底部：保存 / 取消。
    ///
    /// 【双行保存（万总要求：切换配置内容不丢）】
    ///   无论当前启用哪种连接，保存时 TCP、串口两行的参数【都会】写入数据库各自的那一行；
    ///   单选只决定启用标志落在哪里。来回切换只改标志，参数永不互相覆盖。
    ///
    /// 【输入验证（万总要求）】
    ///   TCP：IP 用 IPAddress.TryParse 验证；端口 1~65535；复位端口 0~65535（0=禁用）；
    ///   串口：串口号从 SerialPort.GetPortNames() 实时枚举（无串口时提示并允许手填）；
    ///         波特率下拉 9600/19200/38400/57600/115200。
    ///   校验不过：红字提示 + 焦点定位，整批拒绝保存。
    ///
    /// 【规约】ShowDialog 打开、用完销毁；保存类操作先弹确认框、执行期间禁用按钮。
    /// </summary>
    public class PrinterConfigForm : Form
    {
        // ---------- 控件字段 ----------
        private RadioButton rdoTcp = new RadioButton();
        private RadioButton rdoSerial = new RadioButton();

        private TextBox txtIp = new TextBox();
        private TextBox txtPort = new TextBox();
        private TextBox txtResetPort = new TextBox();

        private ComboBox cboPortName = new ComboBox();
        private ComboBox cboBaudRate = new ComboBox();

        private Button btnSave = new Button();
        private Button btnCancel = new Button();
        private Label lblError = new Label();

        /// <summary>加载到的当前启用类型（回显用）</summary>
        private ConnType _enabledType = ConnType.Tcp;

        /// <summary>构造时加载配置并回显；加载失败提示但窗体仍可打开（显示默认值）</summary>
        public PrinterConfigForm()
        {
            InitializeComponent();
            LoadConfig();
        }

        // ============================================================
        // 布局
        // ============================================================

        private void InitializeComponent()
        {
            this.Text = "喷码机配置";
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(430, 560);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Microsoft YaHei UI", 10F);

            int left = 30;
            int right = 370;

            // ---------- 连接方式 ----------
            Label lblType = new Label();
            lblType.Text = "连接方式：";
            lblType.Location = new Point(left, 20);
            lblType.Size = new Size(80, 25);

            rdoTcp.Name = "rdoTcp";
            rdoTcp.Text = "TCP 网口";
            rdoTcp.Location = new Point(120, 18);
            rdoTcp.Size = new Size(110, 25);
            rdoTcp.Checked = true;

            rdoSerial.Name = "rdoSerial";
            rdoSerial.Text = "串口 RS232";
            rdoSerial.Location = new Point(240, 18);
            rdoSerial.Size = new Size(120, 25);

            // ---------- TCP 参数组 ----------
            GroupBox gbxTcp = new GroupBox();
            gbxTcp.Text = "TCP 参数";
            gbxTcp.Location = new Point(left, 55);
            gbxTcp.Size = new Size(345, 160);

            Label lblIp = new Label();
            lblIp.Text = "IP 地址：";
            lblIp.Location = new Point(15, 33);
            lblIp.Size = new Size(90, 23);

            txtIp.Name = "txtIp";
            txtIp.Location = new Point(110, 29);
            txtIp.Size = new Size(215, 27);

            Label lblPort = new Label();
            lblPort.Text = "端 口：";
            lblPort.Location = new Point(15, 71);
            lblPort.Size = new Size(90, 23);

            txtPort.Name = "txtPort";
            txtPort.Location = new Point(110, 67);
            txtPort.Size = new Size(215, 27);

            Label lblResetPort = new Label();
            lblResetPort.Text = "复位端口：";
            lblResetPort.Location = new Point(15, 109);
            lblResetPort.Size = new Size(95, 23);

            txtResetPort.Name = "txtResetPort";
            txtResetPort.Location = new Point(110, 105);
            txtResetPort.Size = new Size(215, 27);

            gbxTcp.Controls.Add(lblIp);
            gbxTcp.Controls.Add(txtIp);
            gbxTcp.Controls.Add(lblPort);
            gbxTcp.Controls.Add(txtPort);
            gbxTcp.Controls.Add(lblResetPort);
            gbxTcp.Controls.Add(txtResetPort);

            // ---------- 串口参数组 ----------
            GroupBox gbxSerial = new GroupBox();
            gbxSerial.Text = "串口参数";
            gbxSerial.Location = new Point(left, 225);
            gbxSerial.Size = new Size(345, 160);

            Label lblPortName = new Label();
            lblPortName.Text = "串 口 号：";
            lblPortName.Location = new Point(15, 33);
            lblPortName.Size = new Size(90, 23);

            cboPortName.Name = "cboPortName";
            cboPortName.Location = new Point(110, 29);
            cboPortName.Size = new Size(215, 27);
            cboPortName.DropDownStyle = ComboBoxStyle.DropDown;

            Label lblBaud = new Label();
            lblBaud.Text = "波特率：";
            lblBaud.Location = new Point(15, 71);
            lblBaud.Size = new Size(90, 23);

            cboBaudRate.Name = "cboBaudRate";
            cboBaudRate.Location = new Point(110, 67);
            cboBaudRate.Size = new Size(215, 27);
            cboBaudRate.DropDownStyle = ComboBoxStyle.DropDownList;

            Label lblSerialTip = new Label();
            lblSerialTip.Text = "串口参数固定 8 数据位 / 1 停止位 / 无校验";
            lblSerialTip.ForeColor = Color.Gray;
            lblSerialTip.Location = new Point(15, 109);
            lblSerialTip.Size = new Size(310, 23);

            gbxSerial.Controls.Add(lblPortName);
            gbxSerial.Controls.Add(cboPortName);
            gbxSerial.Controls.Add(lblBaud);
            gbxSerial.Controls.Add(cboBaudRate);
            gbxSerial.Controls.Add(lblSerialTip);

            // ---------- 错误提示 ----------
            lblError.Name = "lblError";
            lblError.ForeColor = Color.Red;
            lblError.Location = new Point(left, 395);
            lblError.Size = new Size(345, 45);

            // ---------- 按钮 ----------
            btnSave.Name = "btnSave";
            btnSave.Text = "保存";
            btnSave.Location = new Point(105, 445);
            btnSave.Size = new Size(90, 36);
            btnSave.Click += new EventHandler(btnSave_Click);

            btnCancel.Name = "btnCancel";
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(225, 445);
            btnCancel.Size = new Size(90, 36);
            btnCancel.Click += new EventHandler(btnCancel_Click);

            this.Controls.Add(lblType);
            this.Controls.Add(rdoTcp);
            this.Controls.Add(rdoSerial);
            this.Controls.Add(gbxTcp);
            this.Controls.Add(gbxSerial);
            this.Controls.Add(lblError);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnCancel);
        }

        // ============================================================
        // 加载与回显
        // ============================================================

        /// <summary>
        /// 加载配置并回显（加载失败：提示 + 默认值，窗体仍可用）
        /// 【串口号来源】SerialPort.GetPortNames() 实时枚举本机串口；
        ///   数据库里存的串口号若不在本机清单中（换机器场景），仍加进下拉保持回显不丢。
        /// </summary>
        private void LoadConfig()
        {
            PrinterConfigBLL.LoadResult load = PrinterConfigBLL.Load();

            if (!load.Success)
            {
                lblError.Text = load.Message;
            }

            // TCP 回显
            txtIp.Text = load.TcpConfig.TcpIp;
            txtPort.Text = load.TcpConfig.TcpPort.ToString();
            txtResetPort.Text = load.TcpConfig.ResetPort.ToString();

            // 串口回显：本机串口清单 + 库中保存值（可能不在本机清单，保进来不丢）
            cboPortName.Items.Clear();
            try
            {
                string[] ports = System.IO.Ports.SerialPort.GetPortNames();
                for (int i = 0; i < ports.Length; i++)
                {
                    if (!cboPortName.Items.Contains(ports[i]))
                    {
                        cboPortName.Items.Add(ports[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                // 枚举串口失败不致命（无串口的机器），只记日志
                LogHelper.Instance.Warn("枚举本机串口失败：" + ex.Message);
            }

            string savedPort = load.SerialConfig.SerialPortName;
            if (savedPort.Length > 0 && !cboPortName.Items.Contains(savedPort))
            {
                cboPortName.Items.Add(savedPort);
            }
            cboPortName.Text = savedPort;

            cboBaudRate.Items.Clear();
            for (int i = 0; i < PrinterConfigBLL.BaudRates.Length; i++)
            {
                cboBaudRate.Items.Add(PrinterConfigBLL.BaudRates[i].ToString());
            }
            cboBaudRate.Text = load.SerialConfig.SerialBaudRate.ToString();

            // 启用类型回显
            _enabledType = load.EnabledType;
            if (_enabledType == ConnType.Serial)
            {
                rdoSerial.Checked = true;
            }
            else
            {
                rdoTcp.Checked = true;
            }
        }

        // ============================================================
        // 保存
        // ============================================================

        /// <summary>
        /// 保存：读输入 → 组实体 → 确认框 → BLL 校验+写库 → 成功关窗
        /// 【执行期间禁用按钮】（规约：保存类操作执行期间禁用按钮防连点）。
        /// </summary>
        private void btnSave_Click(object sender, EventArgs e)
        {
            lblError.Text = string.Empty;

            // ---------- 读输入并组装两行实体（两行都保存，切换不丢配置） ----------
            PrinterConfig tcpConfig = new PrinterConfig();
            tcpConfig.ConnType = ConnType.Tcp;
            tcpConfig.TcpIp = txtIp.Text.Trim();
            tcpConfig.TcpPort = ParseIntOrZero(txtPort.Text, "端口", lblError);
            if (tcpConfig.TcpPort < 0)
            {
                txtPort.Focus();
                return;
            }
            tcpConfig.ResetPort = ParseIntOrZero(txtResetPort.Text, "复位端口", lblError);
            if (tcpConfig.ResetPort < 0)
            {
                txtResetPort.Focus();
                return;
            }

            PrinterConfig serialConfig = new PrinterConfig();
            serialConfig.ConnType = ConnType.Serial;
            serialConfig.SerialPortName = cboPortName.Text.Trim();
            int baud = 0;
            if (!int.TryParse(cboBaudRate.Text.Trim(), out baud))
            {
                lblError.Text = "波特率必须是整数，请从下拉中选择。";
                cboBaudRate.Focus();
                return;
            }
            serialConfig.SerialBaudRate = baud;

            // ---------- 启用类型 ----------
            ConnType enabledType = rdoSerial.Checked ? ConnType.Serial : ConnType.Tcp;

            // ---------- 确认框（写操作先确认，规约要求） ----------
            string enableText = enabledType == ConnType.Tcp
                ? "TCP 网口（" + tcpConfig.TcpIp + ":" + tcpConfig.TcpPort.ToString() + "）"
                : "串口 RS232（" + serialConfig.SerialPortName + " @" + serialConfig.SerialBaudRate.ToString() + "）";

            string confirmText = "确认保存喷码机配置？\r\n\r\n"
                                 + "保存后启用：" + enableText + "\r\n\r\n"
                                 + "（TCP 与串口两行参数都会保存，切换时配置不会丢失）";

            if (!MessageHelper.ShowConfirm(confirmText))
            {
                return;
            }

            btnSave.Enabled = false;
            btnCancel.Enabled = false;

            try
            {
                string error = PrinterConfigBLL.Save(tcpConfig, serialConfig, enabledType);

                if (error.Length == 0)
                {
                    MessageHelper.ShowInfo("喷码机配置保存成功。\r\n\r\n当前启用：" + enableText);
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                else
                {
                    lblError.Text = error;
                }
            }
            finally
            {
                btnSave.Enabled = true;
                btnCancel.Enabled = true;
            }
        }

        /// <summary>取消：直接关窗不落库</summary>
        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        /// <summary>
        /// 解析整数输入
        /// 【返回】合法返回数值；非法返回 -1 并在 lblError 显示红字提示（-1 会被端口校验拦住）。
        /// </summary>
        private int ParseIntOrZero(string text, string fieldName, Label errorLabel)
        {
            int value = 0;
            if (!int.TryParse((text ?? string.Empty).Trim(), out value))
            {
                errorLabel.Text = fieldName + "必须是整数，当前输入：" + (text ?? string.Empty).Trim();
                return -1;
            }
            return value;
        }
    }
}

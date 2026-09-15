using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;
using InkPrinterCode.Model.Enums;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] Inkjet printer connection configuration form (TCP / serial, choose one)
    ///
    /// [UI structure]
    ///   Top: connection type radio buttons (rdoTcp / rdoSerial) -- whichever is selected gets IsEnabled=1 on save;
    ///   Middle: TCP parameter group (IP / port / reset port) + serial parameter group (port name dropdown / baud rate dropdown);
    ///   Bottom: Save / Cancel.
    ///
    /// [Two-row save (requirement: switching must not lose configuration)]
    ///   Regardless of which connection is currently enabled, on save the parameters of BOTH the TCP row and the
    ///   serial row [are] written to their respective database rows; the radio buttons only decide where the enabled
    ///   flag goes. Switching back and forth only changes the flag; parameters never overwrite each other.
    ///
    /// [Input validation (requirement)]
    ///   TCP: IP validated with IPAddress.TryParse; port 1~65535; reset port 0~65535 (0 = disabled);
    ///   Serial: port name enumerated live from SerialPort.GetPortNames() (when there is no serial port, show a tip
    ///           and allow manual entry);
    ///           baud rate dropdown 9600/19200/38400/57600/115200.
    ///   On validation failure: red text hint + focus positioning, and the whole batch is refused.
    ///
    /// [Convention] Opened with ShowDialog and disposed after use; save-type operations show a confirmation box first
    /// and disable the buttons while running.
    /// </summary>
    public class PrinterConfigForm : Form
    {
        // ---------- Control fields ----------
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

        /// <summary>Currently enabled type, as loaded (for display)</summary>
        private ConnType _enabledType = ConnType.Tcp;

        /// <summary>Load configuration and populate on construction; on load failure show a tip but the form still opens (showing defaults)</summary>
        public PrinterConfigForm()
        {
            InitializeComponent();
            LoadConfig();
        }

        // ============================================================
        // Layout
        // ============================================================

        private void InitializeComponent()
        {
            this.Text = "Printer Config";
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(430, 560);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Font = new Font("Microsoft YaHei UI", 10F);

            int left = 30;
            int right = 370;

            // ---------- Connection type ----------
            Label lblType = new Label();
            lblType.Text = "Connection:";
            lblType.Location = new Point(left, 20);
            lblType.Size = new Size(80, 25);

            rdoTcp.Name = "rdoTcp";
            rdoTcp.Text = "TCP Network";
            rdoTcp.Location = new Point(120, 18);
            rdoTcp.Size = new Size(110, 25);
            rdoTcp.Checked = true;

            rdoSerial.Name = "rdoSerial";
            rdoSerial.Text = "Serial RS232";
            rdoSerial.Location = new Point(240, 18);
            rdoSerial.Size = new Size(120, 25);

            // ---------- TCP parameter group ----------
            GroupBox gbxTcp = new GroupBox();
            gbxTcp.Text = "TCP Parameters";
            gbxTcp.Location = new Point(left, 55);
            gbxTcp.Size = new Size(345, 160);

            Label lblIp = new Label();
            lblIp.Text = "IP address:";
            lblIp.Location = new Point(15, 33);
            lblIp.Size = new Size(90, 23);

            txtIp.Name = "txtIp";
            txtIp.Location = new Point(110, 29);
            txtIp.Size = new Size(215, 27);

            Label lblPort = new Label();
            lblPort.Text = "Port:";
            lblPort.Location = new Point(15, 71);
            lblPort.Size = new Size(90, 23);

            txtPort.Name = "txtPort";
            txtPort.Location = new Point(110, 67);
            txtPort.Size = new Size(215, 27);

            Label lblResetPort = new Label();
            lblResetPort.Text = "Reset port:";
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

            // ---------- Serial parameter group ----------
            GroupBox gbxSerial = new GroupBox();
            gbxSerial.Text = "Serial Parameters";
            gbxSerial.Location = new Point(left, 225);
            gbxSerial.Size = new Size(345, 160);

            Label lblPortName = new Label();
            lblPortName.Text = "Port name:";
            lblPortName.Location = new Point(15, 33);
            lblPortName.Size = new Size(90, 23);

            cboPortName.Name = "cboPortName";
            cboPortName.Location = new Point(110, 29);
            cboPortName.Size = new Size(215, 27);
            cboPortName.DropDownStyle = ComboBoxStyle.DropDown;

            Label lblBaud = new Label();
            lblBaud.Text = "Baud rate:";
            lblBaud.Location = new Point(15, 71);
            lblBaud.Size = new Size(90, 23);

            cboBaudRate.Name = "cboBaudRate";
            cboBaudRate.Location = new Point(110, 67);
            cboBaudRate.Size = new Size(215, 27);
            cboBaudRate.DropDownStyle = ComboBoxStyle.DropDownList;

            Label lblSerialTip = new Label();
            lblSerialTip.Text = "Serial parameters are fixed at 8 data bits / 1 stop bit / no parity";
            lblSerialTip.ForeColor = Color.Gray;
            lblSerialTip.Location = new Point(15, 109);
            lblSerialTip.Size = new Size(310, 23);

            gbxSerial.Controls.Add(lblPortName);
            gbxSerial.Controls.Add(cboPortName);
            gbxSerial.Controls.Add(lblBaud);
            gbxSerial.Controls.Add(cboBaudRate);
            gbxSerial.Controls.Add(lblSerialTip);

            // ---------- Error hint ----------
            lblError.Name = "lblError";
            lblError.ForeColor = Color.Red;
            lblError.Location = new Point(left, 395);
            lblError.Size = new Size(345, 45);

            // ---------- Buttons ----------
            btnSave.Name = "btnSave";
            btnSave.Text = "Save";
            btnSave.Location = new Point(105, 445);
            btnSave.Size = new Size(90, 36);
            btnSave.Click += new EventHandler(btnSave_Click);

            btnCancel.Name = "btnCancel";
            btnCancel.Text = "Cancel";
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
        // Load and populate
        // ============================================================

        /// <summary>
        /// Load configuration and populate (on load failure: tip + defaults, the form stays usable)
        /// [Port name source] SerialPort.GetPortNames() enumerates the local serial ports live;
        ///   if the port name stored in the database is not in the local list (machine swap scenario), it is still
        ///   added to the dropdown so nothing is lost during display.
        /// </summary>
        private void LoadConfig()
        {
            PrinterConfigBLL.LoadResult load = PrinterConfigBLL.Load();

            if (!load.Success)
            {
                lblError.Text = load.Message;
            }

            // TCP display
            txtIp.Text = load.TcpConfig.TcpIp;
            txtPort.Text = load.TcpConfig.TcpPort.ToString();
            txtResetPort.Text = load.TcpConfig.ResetPort.ToString();

            // Serial display: local port list + the value saved in the DB (may not be in the local list; keep it so nothing is lost)
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
                // Failing to enumerate serial ports is not fatal (machines without a serial port); only log it
                LogHelper.Instance.Warn("Failed to enumerate local serial ports: " + ex.Message);
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

            // Enabled type display
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
        // Save
        // ============================================================

        /// <summary>
        /// Save: read input -> build entities -> confirmation box -> BLL validation + DB write -> close on success
        /// [Buttons disabled while running] (convention: save-type operations disable the buttons while running to prevent repeated clicks).
        /// </summary>
        private void btnSave_Click(object sender, EventArgs e)
        {
            lblError.Text = string.Empty;

            // ---------- Read input and build both row entities (both rows are saved, so switching loses nothing) ----------
            PrinterConfig tcpConfig = new PrinterConfig();
            tcpConfig.ConnType = ConnType.Tcp;
            tcpConfig.TcpIp = txtIp.Text.Trim();
            tcpConfig.TcpPort = ParseIntOrZero(txtPort.Text, "Port", lblError);
            if (tcpConfig.TcpPort < 0)
            {
                txtPort.Focus();
                return;
            }
            tcpConfig.ResetPort = ParseIntOrZero(txtResetPort.Text, "Reset port", lblError);
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
                lblError.Text = "The baud rate must be an integer. Please select one from the dropdown.";
                cboBaudRate.Focus();
                return;
            }
            serialConfig.SerialBaudRate = baud;

            // ---------- Enabled type ----------
            ConnType enabledType = rdoSerial.Checked ? ConnType.Serial : ConnType.Tcp;

            // ---------- Confirmation box (confirm write operations first, per convention) ----------
            string enableText = enabledType == ConnType.Tcp
                ? "TCP Network (" + tcpConfig.TcpIp + ":" + tcpConfig.TcpPort.ToString() + ")"
                : "Serial RS232 (" + serialConfig.SerialPortName + " @" + serialConfig.SerialBaudRate.ToString() + ")";

            string confirmText = "Confirm saving the printer configuration?\r\n\r\n"
                                 + "Enabled after saving: " + enableText + "\r\n\r\n"
                                 + "(Both the TCP and serial rows are saved, so the configuration is not lost when switching)";

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
                    MessageHelper.ShowInfo("Printer configuration saved successfully.\r\n\r\nCurrently enabled: " + enableText);
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

        /// <summary>Cancel: close the form without writing to the DB</summary>
        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        /// <summary>
        /// Parse an integer input
        /// [Return] A valid number when parseable; -1 when invalid, with a red hint shown in lblError (-1 is then caught by the port validation).
        /// </summary>
        private int ParseIntOrZero(string text, string fieldName, Label errorLabel)
        {
            int value = 0;
            if (!int.TryParse((text ?? string.Empty).Trim(), out value))
            {
                errorLabel.Text = fieldName + " must be an integer. Current input: " + (text ?? string.Empty).Trim();
                return -1;
            }
            return value;
        }
    }
}

namespace InkPrinterCode
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            btnImportData = new Button();
            gbxPrinterOperation = new GroupBox();
            btnPrintConfig = new Button();
            btnSystemConfig = new Button();
            btnStop = new Button();
            btnStart = new Button();
            btnTestPrint = new Button();
            gbxDashboard = new GroupBox();
            lblSendCount = new Label();
            label4 = new Label();
            lblPrinterStatus = new Label();
            label3 = new Label();
            txtRunLog = new TextBox();
            lblStatus = new Label();
            label1 = new Label();
            lblPrintedCount = new Label();
            lblPrintedTitle = new Label();
            lblAvailableCount = new Label();
            lblAvailableTitle = new Label();
            btnDataView = new Button();
            lblImportTip = new Label();
            gbxPrinterOperation.SuspendLayout();
            gbxDashboard.SuspendLayout();
            SuspendLayout();
            // 
            // btnImportData
            // 
            btnImportData.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnImportData.Location = new Point(33, 31);
            btnImportData.Name = "btnImportData";
            btnImportData.Size = new Size(112, 43);
            btnImportData.TabIndex = 0;
            btnImportData.Text = "数据导入";
            btnImportData.UseVisualStyleBackColor = true;
            // 
            // gbxPrinterOperation
            // 
            gbxPrinterOperation.Controls.Add(btnPrintConfig);
            gbxPrinterOperation.Controls.Add(btnSystemConfig);
            gbxPrinterOperation.Controls.Add(btnStop);
            gbxPrinterOperation.Controls.Add(btnStart);
            gbxPrinterOperation.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            gbxPrinterOperation.Location = new Point(869, 44);
            gbxPrinterOperation.Name = "gbxPrinterOperation";
            gbxPrinterOperation.Size = new Size(200, 594);
            gbxPrinterOperation.TabIndex = 1;
            gbxPrinterOperation.TabStop = false;
            gbxPrinterOperation.Text = "喷码机操作";
            // 
            // btnPrintConfig
            // 
            btnPrintConfig.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnPrintConfig.Location = new Point(35, 32);
            btnPrintConfig.Name = "btnPrintConfig";
            btnPrintConfig.Size = new Size(159, 43);
            btnPrintConfig.TabIndex = 5;
            btnPrintConfig.Text = "喷码机配置";
            btnPrintConfig.UseVisualStyleBackColor = true;
            // 
            // btnSystemConfig
            // 
            btnSystemConfig.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
            btnSystemConfig.Location = new Point(35, 85);
            btnSystemConfig.Name = "btnSystemConfig";
            btnSystemConfig.Size = new Size(159, 43);
            btnSystemConfig.TabIndex = 6;
            btnSystemConfig.Text = "系统参数";
            btnSystemConfig.UseVisualStyleBackColor = true;
            // 
            // btnStop
            // 
            btnStop.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnStop.Location = new Point(35, 499);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(112, 43);
            btnStop.TabIndex = 4;
            btnStop.Text = "结束喷码";
            btnStop.UseVisualStyleBackColor = true;
            // 
            // btnStart
            // 
            btnStart.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnStart.Location = new Point(35, 408);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(112, 43);
            btnStart.TabIndex = 3;
            btnStart.Text = "开始喷码";
            btnStart.UseVisualStyleBackColor = true;
            // 
            // btnTestPrint
            // 
            btnTestPrint.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnTestPrint.Location = new Point(732, 31);
            btnTestPrint.Name = "btnTestPrint";
            btnTestPrint.Size = new Size(112, 43);
            btnTestPrint.TabIndex = 7;
            btnTestPrint.Text = "测试喷印";
            btnTestPrint.UseVisualStyleBackColor = true;
            // 
            // gbxDashboard
            // 
            gbxDashboard.Controls.Add(lblSendCount);
            gbxDashboard.Controls.Add(label4);
            gbxDashboard.Controls.Add(lblPrinterStatus);
            gbxDashboard.Controls.Add(label3);
            gbxDashboard.Controls.Add(txtRunLog);
            gbxDashboard.Controls.Add(lblStatus);
            gbxDashboard.Controls.Add(label1);
            gbxDashboard.Controls.Add(lblPrintedCount);
            gbxDashboard.Controls.Add(lblPrintedTitle);
            gbxDashboard.Controls.Add(lblAvailableCount);
            gbxDashboard.Controls.Add(lblAvailableTitle);
            gbxDashboard.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            gbxDashboard.Location = new Point(33, 103);
            gbxDashboard.Name = "gbxDashboard";
            gbxDashboard.Size = new Size(811, 535);
            gbxDashboard.TabIndex = 2;
            gbxDashboard.TabStop = false;
            gbxDashboard.Text = "生产看板";
            // 
            // lblSendCount
            // 
            lblSendCount.AutoSize = true;
            lblSendCount.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblSendCount.Location = new Point(233, 179);
            lblSendCount.Name = "lblSendCount";
            lblSendCount.Size = new Size(31, 35);
            lblSendCount.TabIndex = 15;
            lblSendCount.Text = "0";
            lblSendCount.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            label4.Location = new Point(22, 183);
            label4.Name = "label4";
            label4.Size = new Size(92, 27);
            label4.TabIndex = 14;
            label4.Text = "发码数量";
            // 
            // lblPrinterStatus
            // 
            lblPrinterStatus.AutoSize = true;
            lblPrinterStatus.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblPrinterStatus.ForeColor = Color.FromArgb(0, 64, 0);
            lblPrinterStatus.Location = new Point(481, 59);
            lblPrinterStatus.Name = "lblPrinterStatus";
            lblPrinterStatus.Size = new Size(96, 35);
            lblPrinterStatus.TabIndex = 13;
            lblPrinterStatus.Text = "未连接";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            label3.Location = new Point(332, 59);
            label3.Name = "label3";
            label3.Size = new Size(112, 27);
            label3.TabIndex = 12;
            label3.Text = "喷码机状态";
            // 
            // txtRunLog
            // 
            txtRunLog.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            txtRunLog.Location = new Point(22, 233);
            txtRunLog.Multiline = true;
            txtRunLog.Name = "txtRunLog";
            txtRunLog.Size = new Size(744, 296);
            txtRunLog.TabIndex = 11;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblStatus.ForeColor = Color.FromArgb(0, 64, 0);
            lblStatus.Location = new Point(202, 59);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(96, 35);
            lblStatus.TabIndex = 10;
            lblStatus.Text = "未启动";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            label1.Location = new Point(22, 59);
            label1.Name = "label1";
            label1.Size = new Size(92, 27);
            label1.TabIndex = 9;
            label1.Text = "生产状态";
            // 
            // lblPrintedCount
            // 
            lblPrintedCount.AutoSize = true;
            lblPrintedCount.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblPrintedCount.Location = new Point(705, 175);
            lblPrintedCount.Name = "lblPrintedCount";
            lblPrintedCount.Size = new Size(31, 35);
            lblPrintedCount.TabIndex = 3;
            lblPrintedCount.Text = "0";
            lblPrintedCount.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // lblPrintedTitle
            // 
            lblPrintedTitle.AutoSize = true;
            lblPrintedTitle.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            lblPrintedTitle.Location = new Point(494, 179);
            lblPrintedTitle.Name = "lblPrintedTitle";
            lblPrintedTitle.Size = new Size(72, 27);
            lblPrintedTitle.TabIndex = 2;
            lblPrintedTitle.Text = "喷印数";
            // 
            // lblAvailableCount
            // 
            lblAvailableCount.AutoSize = true;
            lblAvailableCount.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblAvailableCount.ForeColor = Color.FromArgb(0, 64, 0);
            lblAvailableCount.Location = new Point(233, 127);
            lblAvailableCount.Name = "lblAvailableCount";
            lblAvailableCount.Size = new Size(31, 35);
            lblAvailableCount.TabIndex = 1;
            lblAvailableCount.Text = "0";
            // 
            // lblAvailableTitle
            // 
            lblAvailableTitle.AutoSize = true;
            lblAvailableTitle.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            lblAvailableTitle.Location = new Point(22, 127);
            lblAvailableTitle.Name = "lblAvailableTitle";
            lblAvailableTitle.Size = new Size(152, 27);
            lblAvailableTitle.TabIndex = 0;
            lblAvailableTitle.Text = "有机码可用数量";
            // 
            // btnDataView
            // 
            btnDataView.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnDataView.Location = new Point(458, 31);
            btnDataView.Name = "btnDataView";
            btnDataView.Size = new Size(112, 43);
            btnDataView.TabIndex = 3;
            btnDataView.Text = "数据查看";
            btnDataView.UseVisualStyleBackColor = true;
            // 
            // lblImportTip
            // 
            lblImportTip.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            lblImportTip.ForeColor = Color.FromArgb(192, 0, 0);
            lblImportTip.Location = new Point(151, 31);
            lblImportTip.Name = "lblImportTip";
            lblImportTip.Size = new Size(301, 62);
            lblImportTip.TabIndex = 9;
            lblImportTip.Text = "【导入提醒】Excel 码值列请务必设为「文本」格式：\r\n超 15 位的长码若存成数值，Excel 自身会丢失尾数，\r\n导入后无法还原，只能删除重导！";
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1102, 650);
            Controls.Add(lblImportTip);
            Controls.Add(btnTestPrint);
            Controls.Add(btnDataView);
            Controls.Add(gbxDashboard);
            Controls.Add(gbxPrinterOperation);
            Controls.Add(btnImportData);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MdiChildrenMinimizedAnchorBottom = false;
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "喷码机发码软件V2026091203";
            gbxPrinterOperation.ResumeLayout(false);
            gbxDashboard.ResumeLayout(false);
            gbxDashboard.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        // [2026-09-10] 控件按开发规约命名前缀统一改名：groupBox→gbx、label→lbl、button→btn
        //   同时删除了原 label7 —— 它无文本、Size 为 0，且与 label9 坐标完全重叠（都是 22,292），属于多余控件。
        private Button btnImportData;
        private GroupBox gbxPrinterOperation;
        private GroupBox gbxDashboard;
        private Label lblAvailableTitle;
        private Button btnStart;
        private Label lblPrintedCount;
        private Label lblPrintedTitle;
        private Label lblAvailableCount;
        private Button btnStop;
        private Button btnDataView;

        // [2026-09-10] Excel 长码精度红色提醒（常驻主界面，见 InitializeComponent 内注释）
        private Label lblImportTip;
        private Button btnPrintConfig;

        // [2026-09-10] 阶段二新增：系统参数配置入口 + 测试喷印按钮
        private Button btnSystemConfig;
        private Button btnTestPrint;

        private Label label1;
        private Label lblStatus;
        private TextBox txtRunLog;
        private Label lblPrinterStatus;
        private Label label3;
        private Label lblSendCount;
        private Label label4;
    }
}

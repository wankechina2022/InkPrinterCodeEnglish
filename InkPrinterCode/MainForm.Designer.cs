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
            btnImportData.Font = new Font("Microsoft YaHei UI", 15F);
            btnImportData.Location = new Point(52, 44);
            btnImportData.Margin = new Padding(5, 4, 5, 4);
            btnImportData.Name = "btnImportData";
            btnImportData.Size = new Size(305, 61);
            btnImportData.TabIndex = 0;
            btnImportData.Text = "Data Import";
            btnImportData.UseVisualStyleBackColor = true;
            // 
            // gbxPrinterOperation
            // 
            gbxPrinterOperation.Controls.Add(btnPrintConfig);
            gbxPrinterOperation.Controls.Add(btnSystemConfig);
            gbxPrinterOperation.Controls.Add(btnStop);
            gbxPrinterOperation.Controls.Add(btnStart);
            gbxPrinterOperation.Font = new Font("Microsoft YaHei UI", 15F);
            gbxPrinterOperation.Location = new Point(1452, 62);
            gbxPrinterOperation.Margin = new Padding(5, 4, 5, 4);
            gbxPrinterOperation.Name = "gbxPrinterOperation";
            gbxPrinterOperation.Padding = new Padding(5, 4, 5, 4);
            gbxPrinterOperation.Size = new Size(314, 839);
            gbxPrinterOperation.TabIndex = 1;
            gbxPrinterOperation.TabStop = false;
            gbxPrinterOperation.Text = "Printer Operation";
            // 
            // btnPrintConfig
            // 
            btnPrintConfig.Font = new Font("Microsoft YaHei UI", 15F);
            btnPrintConfig.Location = new Point(31, 45);
            btnPrintConfig.Margin = new Padding(5, 4, 5, 4);
            btnPrintConfig.Name = "btnPrintConfig";
            btnPrintConfig.Size = new Size(251, 61);
            btnPrintConfig.TabIndex = 5;
            btnPrintConfig.Text = "Printer Config";
            btnPrintConfig.UseVisualStyleBackColor = true;
            // 
            // btnSystemConfig
            // 
            btnSystemConfig.Font = new Font("Microsoft YaHei UI", 12F);
            btnSystemConfig.Location = new Point(31, 120);
            btnSystemConfig.Margin = new Padding(5, 4, 5, 4);
            btnSystemConfig.Name = "btnSystemConfig";
            btnSystemConfig.Size = new Size(251, 61);
            btnSystemConfig.TabIndex = 6;
            btnSystemConfig.Text = "System Parameters";
            btnSystemConfig.UseVisualStyleBackColor = true;
            // 
            // btnStop
            // 
            btnStop.Font = new Font("Microsoft YaHei UI", 15F);
            btnStop.Location = new Point(31, 704);
            btnStop.Margin = new Padding(5, 4, 5, 4);
            btnStop.Name = "btnStop";
            btnStop.Size = new Size(251, 61);
            btnStop.TabIndex = 4;
            btnStop.Text = "Stop Printing";
            btnStop.UseVisualStyleBackColor = true;
            // 
            // btnStart
            // 
            btnStart.Font = new Font("Microsoft YaHei UI", 15F);
            btnStart.Location = new Point(31, 576);
            btnStart.Margin = new Padding(5, 4, 5, 4);
            btnStart.Name = "btnStart";
            btnStart.Size = new Size(251, 61);
            btnStart.TabIndex = 3;
            btnStart.Text = "Start Printing";
            btnStart.UseVisualStyleBackColor = true;
            // 
            // btnTestPrint
            // 
            btnTestPrint.Font = new Font("Microsoft YaHei UI", 15F);
            btnTestPrint.Location = new Point(1251, 44);
            btnTestPrint.Margin = new Padding(5, 4, 5, 4);
            btnTestPrint.Name = "btnTestPrint";
            btnTestPrint.Size = new Size(176, 61);
            btnTestPrint.TabIndex = 7;
            btnTestPrint.Text = "Test Print";
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
            gbxDashboard.Font = new Font("Microsoft YaHei UI", 15F);
            gbxDashboard.Location = new Point(52, 145);
            gbxDashboard.Margin = new Padding(5, 4, 5, 4);
            gbxDashboard.Name = "gbxDashboard";
            gbxDashboard.Padding = new Padding(5, 4, 5, 4);
            gbxDashboard.Size = new Size(1328, 755);
            gbxDashboard.TabIndex = 2;
            gbxDashboard.TabStop = false;
            gbxDashboard.Text = "Production Dashboard";
            // 
            // lblSendCount
            // 
            lblSendCount.AutoSize = true;
            lblSendCount.Font = new Font("Microsoft YaHei UI", 20F);
            lblSendCount.Location = new Point(487, 253);
            lblSendCount.Margin = new Padding(5, 0, 5, 0);
            lblSendCount.Name = "lblSendCount";
            lblSendCount.Size = new Size(45, 52);
            lblSendCount.TabIndex = 15;
            lblSendCount.Text = "0";
            lblSendCount.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Font = new Font("Microsoft YaHei UI", 15F);
            label4.Location = new Point(35, 258);
            label4.Margin = new Padding(5, 0, 5, 0);
            label4.Name = "label4";
            label4.Size = new Size(178, 39);
            label4.TabIndex = 14;
            label4.Text = "Codes Sent";
            // 
            // lblPrinterStatus
            // 
            lblPrinterStatus.AutoSize = true;
            lblPrinterStatus.Font = new Font("Microsoft YaHei UI", 20F);
            lblPrinterStatus.ForeColor = Color.FromArgb(0, 64, 0);
            lblPrinterStatus.Location = new Point(972, 83);
            lblPrinterStatus.Margin = new Padding(5, 0, 5, 0);
            lblPrinterStatus.Name = "lblPrinterStatus";
            lblPrinterStatus.Size = new Size(284, 52);
            lblPrinterStatus.TabIndex = 13;
            lblPrinterStatus.Text = "Disconnected";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Font = new Font("Microsoft YaHei UI", 15F);
            label3.Location = new Point(708, 94);
            label3.Margin = new Padding(5, 0, 5, 0);
            label3.Name = "label3";
            label3.Size = new Size(208, 39);
            label3.TabIndex = 12;
            label3.Text = "Printer Status";
            // 
            // txtRunLog
            // 
            txtRunLog.Font = new Font("Microsoft YaHei UI", 10F);
            txtRunLog.Location = new Point(35, 329);
            txtRunLog.Margin = new Padding(5, 4, 5, 4);
            txtRunLog.Multiline = true;
            txtRunLog.Name = "txtRunLog";
            txtRunLog.Size = new Size(1256, 416);
            txtRunLog.TabIndex = 11;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Microsoft YaHei UI", 20F);
            lblStatus.ForeColor = Color.FromArgb(0, 64, 0);
            lblStatus.Location = new Point(346, 83);
            lblStatus.Margin = new Padding(5, 0, 5, 0);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(246, 52);
            lblStatus.TabIndex = 10;
            lblStatus.Text = "Not Started";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Microsoft YaHei UI", 15F);
            label1.Location = new Point(35, 83);
            label1.Margin = new Padding(5, 0, 5, 0);
            label1.Name = "label1";
            label1.Size = new Size(270, 39);
            label1.TabIndex = 9;
            label1.Text = "Production Status";
            // 
            // lblPrintedCount
            // 
            lblPrintedCount.AutoSize = true;
            lblPrintedCount.Font = new Font("Microsoft YaHei UI", 20F);
            lblPrintedCount.Location = new Point(1179, 247);
            lblPrintedCount.Margin = new Padding(5, 0, 5, 0);
            lblPrintedCount.Name = "lblPrintedCount";
            lblPrintedCount.Size = new Size(45, 52);
            lblPrintedCount.TabIndex = 3;
            lblPrintedCount.Text = "0";
            lblPrintedCount.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // lblPrintedTitle
            // 
            lblPrintedTitle.AutoSize = true;
            lblPrintedTitle.Font = new Font("Microsoft YaHei UI", 15F);
            lblPrintedTitle.Location = new Point(880, 253);
            lblPrintedTitle.Margin = new Padding(5, 0, 5, 0);
            lblPrintedTitle.Name = "lblPrintedTitle";
            lblPrintedTitle.Size = new Size(214, 39);
            lblPrintedTitle.TabIndex = 2;
            lblPrintedTitle.Text = "Printed Count";
            // 
            // lblAvailableCount
            // 
            lblAvailableCount.AutoSize = true;
            lblAvailableCount.Font = new Font("Microsoft YaHei UI", 20F);
            lblAvailableCount.ForeColor = Color.FromArgb(0, 64, 0);
            lblAvailableCount.Location = new Point(487, 179);
            lblAvailableCount.Margin = new Padding(5, 0, 5, 0);
            lblAvailableCount.Name = "lblAvailableCount";
            lblAvailableCount.Size = new Size(45, 52);
            lblAvailableCount.TabIndex = 1;
            lblAvailableCount.Text = "0";
            // 
            // lblAvailableTitle
            // 
            lblAvailableTitle.AutoSize = true;
            lblAvailableTitle.Font = new Font("Microsoft YaHei UI", 15F);
            lblAvailableTitle.Location = new Point(35, 179);
            lblAvailableTitle.Margin = new Padding(5, 0, 5, 0);
            lblAvailableTitle.Name = "lblAvailableTitle";
            lblAvailableTitle.Size = new Size(367, 39);
            lblAvailableTitle.TabIndex = 0;
            lblAvailableTitle.Text = "Available Organic Codes";
            // 
            // btnDataView
            // 
            btnDataView.Font = new Font("Microsoft YaHei UI", 15F);
            btnDataView.Location = new Point(999, 44);
            btnDataView.Margin = new Padding(5, 4, 5, 4);
            btnDataView.Name = "btnDataView";
            btnDataView.Size = new Size(176, 61);
            btnDataView.TabIndex = 3;
            btnDataView.Text = "Data View";
            btnDataView.UseVisualStyleBackColor = true;
            // 
            // lblImportTip
            // 
            lblImportTip.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblImportTip.ForeColor = Color.FromArgb(192, 0, 0);
            lblImportTip.Location = new Point(377, 45);
            lblImportTip.Margin = new Padding(5, 0, 5, 0);
            lblImportTip.Name = "lblImportTip";
            lblImportTip.Size = new Size(600, 96);
            lblImportTip.TabIndex = 9;
            lblImportTip.Text = "[Import Reminder] In Excel, set the code column to \"Text\";\r\ncodes over 15 digits lose trailing digits if stored as numbers,\r\nand the loss cannot be undone; delete and re-import the data!";
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(11F, 24F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1801, 918);
            Controls.Add(lblImportTip);
            Controls.Add(btnTestPrint);
            Controls.Add(btnDataView);
            Controls.Add(gbxDashboard);
            Controls.Add(gbxPrinterOperation);
            Controls.Add(btnImportData);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Margin = new Padding(5, 4, 5, 4);
            MaximizeBox = false;
            MdiChildrenMinimizedAnchorBottom = false;
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Inkjet Printer Code Sending Software V2026091203";
            gbxPrinterOperation.ResumeLayout(false);
            gbxDashboard.ResumeLayout(false);
            gbxDashboard.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        // [2026-09-10] Control names unified per the development convention: groupBox->gbx, label->lbl, button->btn
        //   Also removed the original label7 -- it had no text, Size 0, and its coordinates completely
        //   overlapped label9 (both at 22,292), making it a redundant control.
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

        // [2026-09-10] Red alert for Excel long-code precision (persistent on the main form, see comments in InitializeComponent)
        private Label lblImportTip;
        private Button btnPrintConfig;

        // [2026-09-10] Later additions: system parameter config entry + test print button
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

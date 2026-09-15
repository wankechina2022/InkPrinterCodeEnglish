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
            btnImportData.Text = "Data Import";
            btnImportData.UseVisualStyleBackColor = true;
            // 
            // gbxPrinterOperation
            // 
            gbxPrinterOperation.Controls.Add(btnPrintConfig);
            gbxPrinterOperation.Controls.Add(btnSystemConfig);
            gbxPrinterOperation.Controls.Add(btnStop);
            gbxPrinterOperation.Controls.Add(btnStart);
            gbxPrinterOperation.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            gbxPrinterOperation.Location = new Point(924, 44);
            gbxPrinterOperation.Name = "gbxPrinterOperation";
            gbxPrinterOperation.Size = new Size(200, 594);
            gbxPrinterOperation.TabIndex = 1;
            gbxPrinterOperation.TabStop = false;
            gbxPrinterOperation.Text = "Printer Operation";
            // 
            // btnPrintConfig
            // 
            btnPrintConfig.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnPrintConfig.Location = new Point(20, 32);
            btnPrintConfig.Name = "btnPrintConfig";
            btnPrintConfig.Size = new Size(160, 43); // [2026-09-15] English UI layout fix: was 159
            btnPrintConfig.TabIndex = 5;
            btnPrintConfig.Text = "Printer Config";
            btnPrintConfig.UseVisualStyleBackColor = true;
            // 
            // btnSystemConfig
            // 
            btnSystemConfig.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
            btnSystemConfig.Location = new Point(20, 85);
            btnSystemConfig.Name = "btnSystemConfig";
            // [2026-09-15] Layout fix for the English UI: aligned to x=20 and widened from 159
            //             to 160 to match the other buttons in the Printer Operation panel.
            btnSystemConfig.Size = new Size(160, 43);
            btnSystemConfig.TabIndex = 6;
            btnSystemConfig.Text = "System Parameters";
            btnSystemConfig.UseVisualStyleBackColor = true;
            // 
            // btnStop
            // 
            btnStop.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnStop.Location = new Point(20, 499);
            btnStop.Name = "btnStop";
            // [2026-09-15] Layout fix for the English UI: widened from 112 to 160 so
            //             "Stop Printing" is not clipped inside the button.
            btnStop.Size = new Size(160, 43);
            btnStop.TabIndex = 4;
            btnStop.Text = "Stop Printing";
            btnStop.UseVisualStyleBackColor = true;
            // 
            // btnStart
            // 
            btnStart.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnStart.Location = new Point(20, 408);
            btnStart.Name = "btnStart";
            // [2026-09-15] Layout fix for the English UI: widened from 112 to 160 so
            //             "Start Printing" is not clipped inside the button.
            btnStart.Size = new Size(160, 43);
            btnStart.TabIndex = 3;
            btnStart.Text = "Start Printing";
            btnStart.UseVisualStyleBackColor = true;
            // 
            // btnTestPrint
            // 
            btnTestPrint.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnTestPrint.Location = new Point(796, 31); // [2026-09-15] English UI layout fix: was 732
            btnTestPrint.Name = "btnTestPrint";
            btnTestPrint.Size = new Size(112, 43);
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
            gbxDashboard.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            gbxDashboard.Location = new Point(33, 103);
            gbxDashboard.Name = "gbxDashboard";
            gbxDashboard.Size = new Size(845, 535);
            gbxDashboard.TabIndex = 2;
            gbxDashboard.TabStop = false;
            gbxDashboard.Text = "Production Dashboard";
            // 
            // lblSendCount
            // 
            lblSendCount.AutoSize = true;
            lblSendCount.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblSendCount.Location = new Point(310, 179);
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
            label4.Text = "Codes Sent";
            // 
            // lblPrinterStatus
            // 
            lblPrinterStatus.AutoSize = true;
            lblPrinterStatus.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblPrinterStatus.ForeColor = Color.FromArgb(0, 64, 0);
            lblPrinterStatus.Location = new Point(540, 59); // [2026-09-15] English UI layout fix: was 520
            lblPrinterStatus.Name = "lblPrinterStatus";
            lblPrinterStatus.Size = new Size(96, 35);
            lblPrinterStatus.TabIndex = 13;
            lblPrinterStatus.Text = "Disconnected";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            label3.Location = new Point(380, 59); // [2026-09-15] English UI layout fix: was 360
            label3.Name = "label3";
            label3.Size = new Size(112, 27);
            label3.TabIndex = 12;
            label3.Text = "Printer Status";
            // 
            // txtRunLog
            // 
            txtRunLog.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            txtRunLog.Location = new Point(22, 233);
            txtRunLog.Multiline = true;
            txtRunLog.Name = "txtRunLog";
            txtRunLog.Size = new Size(801, 296); // [2026-09-15] English UI layout fix: was 744
            txtRunLog.TabIndex = 11;
            // 
            // lblStatus
            // 
            lblStatus.AutoSize = true;
            lblStatus.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblStatus.ForeColor = Color.FromArgb(0, 64, 0);
            lblStatus.Location = new Point(220, 59);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(96, 35);
            lblStatus.TabIndex = 10;
            lblStatus.Text = "Not Started";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            label1.Location = new Point(22, 59);
            label1.Name = "label1";
            label1.Size = new Size(92, 27);
            label1.TabIndex = 9;
            label1.Text = "Production Status";
            // 
            // lblPrintedCount
            // 
            lblPrintedCount.AutoSize = true;
            lblPrintedCount.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblPrintedCount.Location = new Point(750, 175);
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
            lblPrintedTitle.Location = new Point(560, 179);
            lblPrintedTitle.Name = "lblPrintedTitle";
            lblPrintedTitle.Size = new Size(72, 27);
            lblPrintedTitle.TabIndex = 2;
            lblPrintedTitle.Text = "Printed Count";
            // 
            // lblAvailableCount
            // 
            lblAvailableCount.AutoSize = true;
            lblAvailableCount.Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            lblAvailableCount.ForeColor = Color.FromArgb(0, 64, 0);
            lblAvailableCount.Location = new Point(310, 127);
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
            lblAvailableTitle.Text = "Available Organic Codes";
            // 
            // btnDataView
            // 
            btnDataView.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Regular, GraphicsUnit.Point);
            btnDataView.Location = new Point(636, 31);
            btnDataView.Name = "btnDataView";
            btnDataView.Size = new Size(112, 43);
            btnDataView.TabIndex = 3;
            btnDataView.Text = "Data View";
            btnDataView.UseVisualStyleBackColor = true;
            // 
            // lblImportTip
            // 
            lblImportTip.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            lblImportTip.ForeColor = Color.FromArgb(192, 0, 0);
            lblImportTip.Location = new Point(151, 31);
            lblImportTip.Name = "lblImportTip";
            // [2026-09-15] Layout fix for the English UI: widened from 301 to 465 so all three
            //             lines of the imported-English reminder render without being clipped.
            lblImportTip.Size = new Size(465, 68); // [2026-09-15] English UI layout fix: was 301x62
            lblImportTip.TabIndex = 9;
            lblImportTip.Text = "[Import Reminder] In Excel, set the code column to \"Text\";\r\ncodes over 15 digits lose trailing digits if stored as numbers,\r\nand the loss cannot be undone; delete and re-import the data!"; // [2026-09-15] reflowed to fit
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            // [2026-09-15] Layout fix for the English UI: widened from 1102x650 so the shifted
            //             Printer Operation panel (right edge 1124) fits with margin.
            ClientSize = new Size(1146, 650);
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

        // [2026-09-10] Phase 2 additions: system parameter config entry + test print button
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

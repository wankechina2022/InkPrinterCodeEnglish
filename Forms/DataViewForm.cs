using System.Data;
using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] 数据查看窗体 —— 码数据查询 / 分页 / 导出 Excel / 按条件批量删除
    ///
    /// 【开发规约落实点】
    ///   1. 子窗体一律 ShowDialog 打开，关闭后由调用方 Dispose（using 包住）；
    ///   2. 写操作（删除）执行前必须弹确认框，并显示具体影响条数；
    ///   3. 执行中的按钮先禁用，避免连点造成重复提交；
    ///   4. 所有异常记 error 日志，界面只显示友好文案，不甩堆栈。
    ///
    /// 【筛选范围（万总确认）】只保留「喷印状态」+「时间范围」两项：
    ///   不做批次筛选、不做码值模糊查询。时间范围按"导入时间"（CreateTime）筛。
    ///
    /// 【时间范围口径】日期选择控件只让用户选到"天"，
    ///   开始时间自动补 00:00:00、结束时间自动补 23:59:59，保证选同一天能筛出整天数据。
    ///
    /// 【删除约束（2026-09-10 调整，双路径）】
    ///   默认路径：未喷（PrintStatus=0）的码一律不删 ——
    ///     确认框显示"可删除 N 条、未喷跳过 M 条"；即便界面判断失误，
    ///     DAL 的 DELETE 语句里还硬带着 PrintStatus &lt;&gt; 0 兜底。
    ///   强制清理路径：勾选"含未喷"后，未喷数据一并删除 ——
    ///     只用于清理导入错误的脏数据（典型：Excel 码值列没设文本格式导致长码丢尾数），
    ///     必须再过一次强确认框，且结果文案里明确标注"含未喷"。
    ///
    /// 【顶部红色提醒的由来】Excel 数字单元格底层是 double，只能精确表示 15~16 位整数。
    ///   码值列若存成"数值"格式，超过 15 位的长码会被 Excel 自身丢掉尾数，
    ///   程序读到的就已经是错值，无法还原 —— 只能靠导入前把列设成"文本"格式规避。
    ///   因此把这条提醒常驻在界面上，而不是写进文档里等人翻。
    /// </summary>
    public class DataViewForm : Form
    {
        // ============================================================
        // 字段
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

        /// <summary>
        /// [2026-09-10] 喷码服务引用（由调用方注入）—— 本窗体只用它读 IsRunning，不做任何控制。
        /// 【边界】允许为 null（例如单元测试或将来别的入口直接 new），
        ///   所有使用点必须先判 null —— 见 ShowRunningServiceTipIfRunning()。
        /// </summary>
        private PrintServiceBLL? _printService = null;

        // ============================================================
        // 构造与布局
        // ============================================================

        /// <summary>
        /// 构造函数
        /// [2026-09-10] 万总需求：打开本窗体时、以及在本窗体里点「查询」「导出Excel」时，
        ///   只要喷码服务正在运行就提示一下（提示但不拦截）。共三处判定，见 ShowRunningServiceTipIfRunning()。
        ///   服务实例必须由调用方（主界面）传入 —— 本窗体自己拿不到。
        /// </summary>
        /// <param name="printService">喷码服务实例；允许传 null，此时不做运行中提示。</param>
        public DataViewForm(PrintServiceBLL printService)
        {
            _printService = printService;
            _pageSize = ConfigHelper.PageSize;

            InitializeComponent();
        }

        /// <summary>
        /// 运行中提示（打开窗体 / 查询 / 导出共用）：判到 Running 才弹
        /// [2026-09-10] 万总裁定口径：
        ///   【只判 Running】严格"运行中"才提示，「启动中 / 停止中」两个过渡态不提示；
        ///   【提示但不拦截】弹一次「确定」框，用户点掉后照常往下执行，绝不 return 拦操作；
        ///   【null 安全】_printService 为 null（调用方未注入）时直接跳过，不弹框。
        /// 【文案来源】统一由 MessageHelper.ShowRunningServiceTip() 提供 ——
        ///   与数据导入那边的提示同属一套文案体系，以后改文案只改 Common 一处。
        /// 【调用点（三处）】① 打开窗体 DataViewForm_Load；② 点「查询」；③ 点「导出Excel」。
        /// 【边界】IsRunning 内部自己加锁取状态，此处直接读即可，不会与状态机打架。
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
        /// 界面布局
        /// 【说明】控件手写创建，不用设计器 —— 结构集中在一个文件里，改布局时不用在设计器与代码之间来回切。
        /// </summary>
        private void InitializeComponent()
        {
            this.Text = "数据查看";
            this.StartPosition = FormStartPosition.CenterParent;
            this.Size = new Size(1020, 706);
            this.MinimumSize = new Size(1020, 540);
            this.Font = new Font("Microsoft YaHei UI", 10F);

            // ---------- 顶部红色提醒条 ----------
            // [2026-09-10] Excel 长码精度提醒常驻界面：
            //   Excel 数字单元格底层是 double，只能精确表示 15~16 位整数，
            //   码值列存成"数值"格式时超 15 位长码会被 Excel 自身丢尾数，程序读到就已经是错值。
            //   这条只能靠导入前把列设成"文本"格式规避，所以放在最显眼的位置而不是写进文档。
            Panel pnlTip = new Panel();
            pnlTip.Dock = DockStyle.Top;
            pnlTip.Height = 46;
            pnlTip.BackColor = Color.FromArgb(255, 238, 238);
            pnlTip.Padding = new Padding(10, 4, 10, 4);

            Label lblTip = new Label();
            lblTip.Dock = DockStyle.Fill;
            lblTip.AutoSize = false;
            lblTip.ForeColor = Color.FromArgb(192, 0, 0);
            lblTip.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            lblTip.TextAlign = ContentAlignment.MiddleLeft;
            lblTip.Text = "【导入提醒】Excel 导入前请务必把码值列设为「文本」格式："
                          + "超过 15 位的长码若存成数值，Excel 自身会丢失尾数，导入后无法还原，只能删除重导。";

            pnlTip.Controls.Add(lblTip);

            // ---------- 筛选区（两行） ----------
            Panel pnlTop = new Panel();
            pnlTop.Dock = DockStyle.Top;
            pnlTop.Height = 96;
            pnlTop.Padding = new Padding(10);

            // 第一行：状态 + 时间范围
            Label lblStatusTitle = new Label();
            lblStatusTitle.Text = "状态：";
            lblStatusTitle.Location = new Point(15, 22);
            lblStatusTitle.Size = new Size(50, 25);

            cboStatus.Name = "cboStatus";
            cboStatus.Location = new Point(65, 18);
            cboStatus.Size = new Size(120, 28);
            cboStatus.DropDownStyle = ComboBoxStyle.DropDownList;
            cboStatus.ValueMember = "Value";
            cboStatus.DisplayMember = "Text";

            chkTime.Name = "chkTime";
            chkTime.Text = "按时间筛选";
            chkTime.Location = new Point(205, 20);
            chkTime.Size = new Size(110, 25);
            chkTime.CheckedChanged += new EventHandler(chkTime_CheckedChanged);

            dtpStart.Name = "dtpStart";
            dtpStart.Location = new Point(325, 18);
            dtpStart.Size = new Size(180, 28);
            dtpStart.Format = DateTimePickerFormat.Custom;
            dtpStart.CustomFormat = "yyyy-MM-dd HH:mm:ss";
            dtpStart.Enabled = false;

            Label lblTo = new Label();
            lblTo.Text = "至";
            lblTo.Location = new Point(512, 22);
            lblTo.Size = new Size(25, 25);

            dtpEnd.Name = "dtpEnd";
            dtpEnd.Location = new Point(542, 18);
            dtpEnd.Size = new Size(180, 28);
            dtpEnd.Format = DateTimePickerFormat.Custom;
            dtpEnd.CustomFormat = "yyyy-MM-dd HH:mm:ss";
            dtpEnd.Enabled = false;

            // 第二行：删除范围开关 + 操作按钮
            // [2026-09-10] "含未喷"开关 —— 默认不勾，未喷数据受保护；
            //   勾上才可删除未喷数据，用于清理导入错误的脏数据（点了还要再过一次强确认）。
            chkIncludeNotPrinted.Name = "chkIncludeNotPrinted";
            chkIncludeNotPrinted.Text = "含未喷数据一起删（清理导错的脏数据用）";
            chkIncludeNotPrinted.Location = new Point(15, 56);
            chkIncludeNotPrinted.Size = new Size(300, 25);
            chkIncludeNotPrinted.ForeColor = Color.FromArgb(192, 0, 0);
            chkIncludeNotPrinted.CheckedChanged += new EventHandler(chkIncludeNotPrinted_CheckedChanged);

            btnQuery.Name = "btnQuery";
            btnQuery.Text = "查询";
            btnQuery.Location = new Point(735, 52);
            btnQuery.Size = new Size(85, 32);
            btnQuery.Click += new EventHandler(btnQuery_Click);

            btnExport.Name = "btnExport";
            btnExport.Text = "导出Excel";
            btnExport.Location = new Point(830, 52);
            btnExport.Size = new Size(85, 32);
            btnExport.Click += new EventHandler(btnExport_Click);

            btnDelete.Name = "btnDelete";
            btnDelete.Text = "删除";
            btnDelete.Location = new Point(925, 52);
            btnDelete.Size = new Size(75, 32);
            btnDelete.ForeColor = Color.Red;
            btnDelete.Click += new EventHandler(btnDelete_Click);

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

            // ---------- 底部分页区 ----------
            Panel pnlBottom = new Panel();
            pnlBottom.Dock = DockStyle.Bottom;
            pnlBottom.Height = 48;

            btnFirst.Name = "btnFirst";
            btnFirst.Text = "首页";
            btnFirst.Location = new Point(15, 9);
            btnFirst.Size = new Size(70, 30);
            btnFirst.Click += new EventHandler(btnFirst_Click);

            btnPrev.Name = "btnPrev";
            btnPrev.Text = "上一页";
            btnPrev.Location = new Point(95, 9);
            btnPrev.Size = new Size(70, 30);
            btnPrev.Click += new EventHandler(btnPrev_Click);

            lblPageInfo.Name = "lblPageInfo";
            lblPageInfo.Text = "第 1 / 1 页";
            lblPageInfo.Location = new Point(180, 14);
            lblPageInfo.Size = new Size(150, 25);

            btnNext.Name = "btnNext";
            btnNext.Text = "下一页";
            btnNext.Location = new Point(340, 9);
            btnNext.Size = new Size(70, 30);
            btnNext.Click += new EventHandler(btnNext_Click);

            btnLast.Name = "btnLast";
            btnLast.Text = "末页";
            btnLast.Location = new Point(420, 9);
            btnLast.Size = new Size(70, 30);
            btnLast.Click += new EventHandler(btnLast_Click);

            lblTotal.Name = "lblTotal";
            lblTotal.Text = "共 0 条";
            lblTotal.Location = new Point(510, 14);
            lblTotal.Size = new Size(400, 25);

            pnlBottom.Controls.Add(btnFirst);
            pnlBottom.Controls.Add(btnPrev);
            pnlBottom.Controls.Add(lblPageInfo);
            pnlBottom.Controls.Add(btnNext);
            pnlBottom.Controls.Add(btnLast);
            pnlBottom.Controls.Add(lblTotal);

            // ---------- 中间数据区 ----------
            dgvData.Name = "dgvData";
            dgvData.Dock = DockStyle.Fill;
            dgvData.ReadOnly = true;
            dgvData.AllowUserToAddRows = false;
            dgvData.AllowUserToDeleteRows = false;
            dgvData.AllowUserToResizeRows = false;
            dgvData.MultiSelect = false;
            dgvData.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvData.RowHeadersVisible = false;
            dgvData.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvData.BackgroundColor = Color.White;

            // [2026-09-10] 万总指令：列名所在行加高。
            // 【机制】ColumnHeadersHeightSizeMode 默认 EnableResizing，行高由系统按字体算
            //   （本窗体 10F 字号下约 23px，文字贴边偏挤）；改为 DisableResizing + 固定 40px，
            //   列头行高不再随字体/DPI 变化。
            // 【边界】只影响列头行，不改数据行高、不改列宽（AutoSizeColumnsMode=Fill 不受影响）。
            dgvData.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            dgvData.ColumnHeadersHeight = 40;

            // 控件加入顺序：WinForms 的 Dock 按 z-order 逆序布局（最后添加的先占边缘），
            // 所以顺序必须是 Fill 区 → 底栏 → 筛选栏 → 提醒条，才能保证提醒条在最顶端。
            this.Controls.Add(dgvData);
            this.Controls.Add(pnlBottom);
            this.Controls.Add(pnlTop);
            this.Controls.Add(pnlTip);

            this.Load += new EventHandler(DataViewForm_Load);
        }

        // ============================================================
        // 事件
        // ============================================================

        /// <summary>
        /// 窗体加载：先做运行中提示，再初始化筛选项默认值并查询第一页
        /// [2026-09-10] 万总指令：打开窗体时也判一次（提示但不拦截，点掉提示后照常加载数据）。
        /// </summary>
        private void DataViewForm_Load(object sender, EventArgs e)
        {
            // [2026-09-10] 万总指令：打开窗体即提示 —— 刻意放在 RefreshData() 之前，
            //   先告知"进这页会读库、与发码取码可能争用"，再真的去读，顺序才符合语义。
            ShowRunningServiceTipIfRunning();

            cboStatus.DataSource = EnumHelper.GetPrintStatusItems(true);

            DateTime today = DateTime.Now.Date;
            dtpStart.Value = today;
            dtpEnd.Value = today;

            RefreshData();
        }

        /// <summary>时间筛选勾选状态变化时，启用/禁用时间控件</summary>
        private void chkTime_CheckedChanged(object sender, EventArgs e)
        {
            dtpStart.Enabled = chkTime.Checked;
            dtpEnd.Enabled = chkTime.Checked;
        }

        /// <summary>
        /// [2026-09-10] "含未喷"开关状态变化 —— 同步把删除按钮改成"强制删除"并加粗
        /// 【目的】让"当前处于高危删除模式"在界面上一眼可见，避免勾了之后忘记、误点删除。
        /// </summary>
        private void chkIncludeNotPrinted_CheckedChanged(object sender, EventArgs e)
        {
            if (chkIncludeNotPrinted.Checked)
            {
                btnDelete.Text = "强制删除";
                btnDelete.Font = new Font(this.Font, FontStyle.Bold);
            }
            else
            {
                btnDelete.Text = "删除";
                btnDelete.Font = new Font(this.Font, FontStyle.Regular);
            }
        }

        /// <summary>查询：回到第一页后重新查询</summary>
        private void btnQuery_Click(object sender, EventArgs e)
        {
            // [2026-09-10] 万总需求：喷码服务运行中先提示一句（提示但不拦截，点掉后继续查询）。
            ShowRunningServiceTipIfRunning();

            _pageIndex = 1;
            RefreshData();
        }

        /// <summary>首页</summary>
        private void btnFirst_Click(object sender, EventArgs e)
        {
            _pageIndex = 1;
            RefreshData();
        }

        /// <summary>上一页</summary>
        private void btnPrev_Click(object sender, EventArgs e)
        {
            if (_pageIndex > 1)
            {
                _pageIndex--;
                RefreshData();
            }
        }

        /// <summary>下一页</summary>
        private void btnNext_Click(object sender, EventArgs e)
        {
            if (_pageIndex < _totalPages)
            {
                _pageIndex++;
                RefreshData();
            }
        }

        /// <summary>末页</summary>
        private void btnLast_Click(object sender, EventArgs e)
        {
            if (_totalPages >= 1)
            {
                _pageIndex = _totalPages;
                RefreshData();
            }
        }

        /// <summary>
        /// 导出 Excel —— 导出当前筛选条件的全量结果（不只是当前页）
        /// </summary>
        private void btnExport_Click(object sender, EventArgs e)
        {
            if (_totalCount <= 0)
            {
                MessageHelper.ShowWarning("当前筛选条件下没有数据，无需导出。");
                return;
            }

            // [2026-09-10] 万总需求：喷码服务运行中先提示一句（提示但不拦截）。
            //   位置刻意放在"无数据"判断之后 —— 点空时只弹"没数据"，不为一次点空弹两个框。
            ShowRunningServiceTipIfRunning();

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出到 Excel";
                dialog.Filter = "Excel文件|*.xlsx";
                dialog.DefaultExt = "xlsx";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = "码数据_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx";

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                btnExport.Enabled = false;
                Cursor oldCursor = this.Cursor;
                this.Cursor = Cursors.WaitCursor;

                try
                {
                    DataTable table = CodeQueryBLL.QueryForExport(BuildFilter());
                    ExcelHelper.ExportDataTable(table, dialog.FileName, "码数据");

                    LogHelper.Instance.Info("导出 Excel 成功，文件=" + dialog.FileName
                                            + "，条数=" + table.Rows.Count.ToString());
                    MessageHelper.ShowInfo("导出成功！\r\n\r\n共 " + table.Rows.Count.ToString()
                                           + " 条\r\n文件位置：" + dialog.FileName);
                }
                catch (Exception ex)
                {
                    LogHelper.Instance.Error("导出 Excel 失败：" + dialog.FileName, ex);
                    MessageHelper.ShowError("导出失败：" + ex.Message
                                            + "\r\n\r\n详情请查看 Logs 目录下的 error 日志。");
                }
                finally
                {
                    this.Cursor = oldCursor;
                    btnExport.Enabled = true;
                }
            }
        }

        /// <summary>
        /// 按当前筛选条件批量删除
        ///
        /// 【双路径】
        ///   默认路径（未勾"含未喷"）：算预览 → 未喷跳过 → 危险确认 → 执行 → 回第一页刷新；
        ///   强制路径（勾了"含未喷"）：未喷一并删除 → 更强的危险确认（写明后果）→ 执行 → 回第一页刷新。
        ///
        /// 【强制路径的用途】导入的数据全是未喷状态。若"未喷不可删"是唯一规则，
        ///   导错的数据（Excel 长码丢尾数之类）就永远删不掉，还占着唯一索引把正确数据堵死。
        ///   所以这条通道必须有，但必须经过显式勾选 + 强确认两道关。
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
                LogHelper.Instance.Error("获取删除预览信息失败", ex);
                MessageHelper.ShowError("无法计算待删除数据：" + ex.Message);
                return;
            }

            if (filteredCount <= 0)
            {
                MessageHelper.ShowWarning("当前筛选条件下没有数据，无需删除。");
                return;
            }

            string message;

            if (allowNotPrinted)
            {
                // 强制清理路径：可删数 = 筛选命中数，全部删除（含未喷）
                message = "【危险操作】即将删除【未喷状态】的码数据！\r\n\r\n"
                          + "  删除条数：" + deletableCount.ToString() + " 条（含未喷数据）\r\n\r\n"
                          + "未喷数据属于待生产资源，删除后必须重新导入才能继续喷码。\r\n"
                          + "本操作仅用于清理导入错误的脏数据（例如 Excel 码值列没设成文本格式、"
                          + "超 15 位长码被 Excel 丢掉尾数）。\r\n\r\n"
                          + "删除后数据不可恢复，确认继续吗？";
            }
            else
            {
                // 默认路径：未喷数据受保护
                if (deletableCount <= 0)
                {
                    MessageHelper.ShowWarning("当前筛选命中的 " + filteredCount.ToString()
                                              + " 条数据全部是未喷状态。\r\n\r\n"
                                              + "未喷状态的码属于待生产资源，默认不允许删除。\r\n\r\n"
                                              + "如果这批数据确实是导错的脏数据需要清理，"
                                              + "请勾选上方「含未喷数据一起删」后再执行删除。");
                    return;
                }

                int skippedCount = filteredCount - deletableCount;

                message = "即将按当前筛选条件删除数据：\r\n\r\n"
                          + "  筛选命中：" + filteredCount.ToString() + " 条\r\n"
                          + "  可删除　：" + deletableCount.ToString() + " 条\r\n"
                          + "  未喷跳过：" + skippedCount.ToString() + " 条\r\n\r\n"
                          + "删除后数据不可恢复，确认继续吗？";
            }

            if (!MessageHelper.ShowDangerConfirm(message))
            {
                LogHelper.Instance.Info("用户取消了批量删除（模式=" + (allowNotPrinted ? "含未喷" : "保护未喷")
                                        + "，筛选命中 " + filteredCount.ToString()
                                        + " 条，可删 " + deletableCount.ToString() + " 条）");
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
        // 私有辅助
        // ============================================================

        /// <summary>按界面上的条件构造查询对象</summary>
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
                // 开始时间取当天 00:00:00，结束时间取当天 23:59:59 —— 选同一天能筛出整天数据
                filter.StartTime = dtpStart.Value.Date.ToDbTimeString();
                filter.EndTime = dtpEnd.Value.Date.AddDays(1).AddSeconds(-1).ToDbTimeString();
            }

            return filter;
        }

        /// <summary>查询并刷新表格与分页信息</summary>
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
                LogHelper.Instance.Error("查询码数据失败", ex);
                MessageHelper.ShowError("查询数据失败：" + ex.Message);
            }
        }

        /// <summary>刷新分页栏文字与按钮可用状态</summary>
        private void UpdatePager()
        {
            lblPageInfo.Text = "第 " + _pageIndex.ToString() + " / " + _totalPages.ToString() + " 页";
            lblTotal.Text = "共 " + _totalCount.ToString() + " 条记录，每页 " + _pageSize.ToString() + " 条";

            btnFirst.Enabled = _pageIndex > 1;
            btnPrev.Enabled = _pageIndex > 1;
            btnNext.Enabled = _pageIndex < _totalPages;
            btnLast.Enabled = _pageIndex < _totalPages;
        }
    }
}

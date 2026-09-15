using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.DAL;
using InkPrinterCode.Forms;
using InkPrinterCode.Model;

namespace InkPrinterCode
{
    /// <summary>
    /// [2026-09-10] 主界面
    ///
    /// 【阶段一接线范围】
    ///   「数据导入」→ 选文件 → 确认 → 进度窗体 → 后台导入 → 结果弹框 → 刷新看板
    ///   「数据查看」→ 打开数据查看窗体（查询 / 分页 / 导出 / 删除）→ 关闭后刷新看板
    ///
    /// 【阶段二接线范围（第 3 轮）】
    ///   「开始喷码」→ PrintServiceBLL.Start()：连接 → 拉起线程 → 信号设置/清队列 → 预填发码 → 心跳/发码
    ///   「结束喷码」→ 确认框 → PrintServiceBLL.Stop()：七步停止流程（线程全退、对象全释放）
    ///   「测试喷印」→ PrintServiceBLL.TestPrint()：发 AB12345 完整缓存帧（仅运行中可用）
    ///   状态联动：未启动=开始可用；启动中/停止中=全锁；运行中=结束+测试可用（万总互斥要求）
    ///   txtRunLog：倒序（最新在上）、超过 RunLogMaxLines 行自动截尾
    ///   lblPrinterStatus：未连接 → 运行中(绿) → 断线(红，心跳超时)
    ///
    /// 【看板数据来源】
    ///   有机码可用数量 = 未喷状态数量（真实查库）
    ///   发码数量（lblSendCount） = 本次运行计数：走到 Write 就 +1，无论写入成败、不看机器反馈
    ///   喷印数（lblPrintedCount） = 本次运行计数：只认机器回传的 0x32（打印完成）
    ///   两个计数均由 PrintServiceBLL 在 Start() 时归零（万总 2026-09-10 20:23 指令）
    ///
    /// 【事件绑定位置】全部写在 MainForm.cs 里手动 +=，不用设计器绑定。
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>喷码服务状态机（阶段二核心，事件在后台线程触发，处理端统一 Invoke）</summary>
        private readonly PrintServiceBLL _printService = new PrintServiceBLL();

        public MainForm()
        {
            InitializeComponent();
            BindEvents();
            BindPrintService();
        }

        // ============================================================
        // 事件绑定
        // ============================================================

        /// <summary>集中绑定所有控件事件</summary>
        private void BindEvents()
        {
            this.Load += new EventHandler(MainForm_Load);
            btnImportData.Click += new EventHandler(btnImportData_Click);
            btnDataView.Click += new EventHandler(btnDataView_Click);

            btnStart.Click += new EventHandler(btnStart_Click);
            btnStop.Click += new EventHandler(btnStop_Click);
            btnTestPrint.Click += new EventHandler(btnTestPrint_Click);

            // [2026-09-10] 补绑两个配置入口按钮（15:07 万总反馈点击无反应 ——
            //   处理方法早已存在，BindEvents 漏绑导致点击无任何动作）
            btnPrintConfig.Click += new EventHandler(btnPrintConfig_Click);
            btnSystemConfig.Click += new EventHandler(btnSystemConfig_Click);

            // 关闭确认：喷码运行中直接关程序会中断生产，必须先问一句
            this.FormClosing += new FormClosingEventHandler(MainForm_FormClosing);

            // 退出兜底：无论从哪里退出，停止流程必须走完（线程全退、连接全关）
            this.FormClosed += new FormClosedEventHandler(MainForm_FormClosed);
        }

        /// <summary>
        /// 订阅喷码服务事件
        /// 【线程说明】事件全部在服务后台线程触发，处理方法内部统一 InvokeRequired 判断回 UI 线程。
        /// </summary>
        private void BindPrintService()
        {
            _printService.RunLog += new Action<string>(PrintService_RunLog);
            _printService.ServiceStateChanged += new Action<string, bool>(PrintService_ServiceStateChanged);
            _printService.PrinterStateChanged += new Action<string, System.Drawing.Color>(PrintService_PrinterStateChanged);
            _printService.DashboardChanged += new Action(PrintService_DashboardChanged);
        }

        /// <summary>窗体加载：初始化运行日志区与按钮初始状态、刷新看板</summary>
        private void MainForm_Load(object sender, EventArgs e)
        {
            // 运行日志区：只读 + 垂直滚动（内容多时不至于看不到历史）
            txtRunLog.ReadOnly = true;
            txtRunLog.ScrollBars = ScrollBars.Vertical;
            txtRunLog.Text = string.Empty;

            // 按钮初始联动：未启动 = 开始 + 测试可用、结束禁用
            // [2026-09-10 20:05] 万总指令：测试喷印已完全独立（自建连接、发完即关），
            //   与开始/结束喷码三方互斥 —— 未启动态即可测试（原来只有运行中可测）
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            btnTestPrint.Enabled = true;

            RefreshDashboard();
        }

        // ============================================================
        // 数据导入
        // ============================================================

        /// <summary>
        /// 「数据导入」按钮 —— 选文件 → 确认 → 后台导入 → 结果弹框
        /// </summary>
        private void btnImportData_Click(object sender, EventArgs e)
        {
            // [2026-09-10] 万总指令：喷码服务运行中先提示一句（提示但不拦截，点掉后继续导入）。
            //   位置刻意放在"选文件"之前 —— 与数据查看页口径一致：一进这个动作就告知风险，
            //   不等用户选完文件才说；导入是整批数据单事务写库、会长时间占库，
            //   故用加重文案 ShowRunningImportTip()（见 Common/MessageHelper.cs）。
            if (_printService != null && _printService.IsRunning)
            {
                MessageHelper.ShowRunningImportTip();
            }

            string filePath = string.Empty;

            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要导入的码数据文件";
                dialog.Filter = "码数据文件|*.txt;*.xls;*.xlsx|文本文件(*.txt)|*.txt|Excel文件|*.xls;*.xlsx";
                dialog.Multiselect = false;
                dialog.CheckFileExists = true;
                dialog.RestoreDirectory = true;

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                filePath = dialog.FileName;
            }

            // 先在界面层拦一道不支持的格式，避免弹出进度框后再报错
            bool supported = false;
            EnumHelper.GetSourceTypeByExtension(filePath, out supported);
            if (!supported)
            {
                MessageHelper.ShowWarning("不支持的文件格式，请选择 .txt / .xls / .xlsx 文件。");
                return;
            }

            string fileName = Path.GetFileName(filePath);

            // [2026-09-10] Excel 长码精度提醒 —— 这是 Excel 自身的 double 精度限制，
            //   程序端无法补救，只能在导入前反复提醒用户把码值列设成"文本"格式。
            bool isExcelFile = filePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase)
                            || filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase);

            string confirmText;
            if (isExcelFile)
            {
                confirmText = "确认导入以下文件？\r\n\r\n" + fileName + "\r\n\r\n"
                              + "【提醒】Excel 导入前请确认码值列已设为「文本」格式：\r\n"
                              + "超 15 位的长码若存成数值，Excel 自身会丢失尾数，\r\n"
                              + "导入后无法还原，只能删除重导！\r\n\r\n"
                              + "导入过程中请勿关闭程序。";
            }
            else
            {
                confirmText = "确认导入以下文件？\r\n\r\n" + fileName
                              + "\r\n\r\n导入过程中请勿关闭程序。";
            }

            if (!MessageHelper.ShowConfirm(confirmText))
            {
                return;
            }

            btnImportData.Enabled = false;

            try
            {
                ImportResult? result = null;

                // 进度窗体用完即销毁（开发规约：子窗体 ShowDialog 后必须释放）
                using (ImportProgressForm progressForm = new ImportProgressForm(filePath))
                {
                    progressForm.ShowDialog(this);
                    result = progressForm.Result;
                }

                if (result == null)
                {
                    MessageHelper.ShowError("导入过程异常结束，未产生结果，请查看 error 日志。");
                    return;
                }

                if (result.Success)
                {
                    MessageHelper.ShowInfo(result.Message);
                }
                else if (result.Canceled)
                {
                    MessageHelper.ShowWarning(result.Message);
                }
                else
                {
                    MessageHelper.ShowError(result.Message);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("导入流程异常", ex);
                MessageHelper.ShowError("导入失败：" + ex.Message);
            }
            finally
            {
                btnImportData.Enabled = true;
                RefreshDashboard();
            }
        }

        // ============================================================
        // 数据查看
        // ============================================================

        /// <summary>
        /// 「数据查看」按钮 —— 打开数据查看窗体
        /// 关闭后刷新看板：在查看窗体里可能执行过删除，数量会变。
        /// </summary>
        private void btnDataView_Click(object sender, EventArgs e)
        {
            try
            {
                // [2026-09-10] 万总需求：数据查看页的查询/导出，在喷码服务运行中要先提示一句。
                //   本窗体自己拿不到服务实例，这里把 _printService 注入进去（只读 IsRunning，不做控制）。
                using (DataViewForm form = new DataViewForm(_printService))
                {
                    form.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("打开数据查看窗体失败", ex);
                MessageHelper.ShowError("打开数据查看失败：" + ex.Message);
            }
            finally
            {
                RefreshDashboard();
            }
        }

        // ============================================================
        // 喷码操作（阶段二第 3 轮接线）
        // ============================================================

        /// <summary>
        /// 「开始喷码」按钮
        /// 【流程】状态机后台执行：读配置 → 建连接 → 拉起常驻线程 → 打印信号设置 → 清三条队列 →
        ///   预填发码（取码即标已喷，占用制）→ 心跳 + 发码循环。
        /// 【失败】连接失败/预填失败自动回"未启动"，原因在运行日志区红色可见。
        /// </summary>
        private void btnStart_Click(object sender, EventArgs e)
        {
            // 无码也允许启动（保持运行态等导入，万总确认）；配置缺失由状态机检测并报回
            _printService.Start();
        }

        /// <summary>
        /// 「结束喷码」按钮 —— 先确认，再走七步停止流程
        /// 【流程】停心跳 → 停线程 → 清喷码机队列 → （v5：不回退任何码）→ 关连接 → 释放 → UI 复原
        /// </summary>
        private void btnStop_Click(object sender, EventArgs e)
        {
            if (!MessageHelper.ShowConfirm("确认结束喷码？\r\n\r\n"
                                           + "将断开与喷码机的连接并清空机内缓存队列。\r\n"
                                           + "已写入喷码机的码保持「已喷」状态，不会重发。"))
            {
                return;
            }

            _printService.Stop();
        }

        /// <summary>
        /// 「测试喷印」按钮 —— 独立建连 → 打印信号设置 → 清三队列 → 发测试码 AB12345 → 关连接
        /// 【万总 19:53 指令】完全独立于生产：自建临时连接、发完即释放；
        ///   只在「未启动」态可用（与开始/结束喷码三方互斥），按钮可用性由 ServiceStateChanged 联动。
        /// </summary>
        private void btnTestPrint_Click(object sender, EventArgs e)
        {
            _printService.TestPrint();
        }

        // ============================================================
        // 喷码服务事件（后台线程触发，统一 Invoke 回 UI 线程）
        // ============================================================

        /// <summary>
        /// 运行日志：时间戳 + 内容，插到 txtRunLog 最上面（万总要求倒序），超行截尾
        /// </summary>
        private void PrintService_RunLog(string message)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(delegate () { AppendRunLog(message); }));
                return;
            }

            AppendRunLog(message);
        }

        /// <summary>实际追加一行运行日志（必须在 UI 线程调用）</summary>
        private void AppendRunLog(string message)
        {
            try
            {
                // [2026-09-10] 万总要求：时间精确到毫秒 —— 便于分辨「发码 → 打印完成」实际间隔
                string stamped = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
                string combined = stamped + Environment.NewLine + txtRunLog.Text;

                // 超过配置行数自动截掉尾部（万总要求：超过 100 行过期数据自动去掉）
                int maxLines = ConfigHelper.RunLogMaxLines;
                string[] lines = combined.Split(new string[] { Environment.NewLine }, StringSplitOptions.None);

                if (lines.Length > maxLines)
                {
                    string[] trimmed = new string[maxLines];
                    Array.Copy(lines, trimmed, maxLines);
                    lines = trimmed;
                }

                txtRunLog.Text = string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("更新运行日志区失败", ex);
            }
        }

        /// <summary>
        /// 生产状态变化：lblStatus 文本与颜色 + 三按钮联动（万总互斥要求）
        ///   未启动 → 开始可用；启动中/停止中 → 全锁；运行中 → 结束+测试可用。
        /// </summary>
        private void PrintService_ServiceStateChanged(string text, bool isRunning)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(delegate () { PrintService_ServiceStateChanged(text, isRunning); }));
                return;
            }

            lblStatus.Text = text;

            if (isRunning)
            {
                lblStatus.ForeColor = System.Drawing.Color.Green;
            }
            else
            {
                lblStatus.ForeColor = System.Drawing.Color.FromArgb(0, 64, 0);
            }

            // 三按钮联动：开始与测试都只在"未启动"放行（两者互斥），结束只在运行中放行
            btnStart.Enabled = (text == "未启动");
            btnStop.Enabled = isRunning;
            btnTestPrint.Enabled = (text == "未启动");
        }

        /// <summary>喷码机状态变化：未连接（暗绿）→ 运行中（绿）→ 断线（红，心跳超时）</summary>
        private void PrintService_PrinterStateChanged(string text, System.Drawing.Color color)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(delegate () { PrintService_PrinterStateChanged(text, color); }));
                return;
            }

            lblPrinterStatus.Text = text;
            lblPrinterStatus.ForeColor = color;
        }

        /// <summary>看板数字变化（发码成功后触发）：重新查库刷新</summary>
        private void PrintService_DashboardChanged()
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(RefreshDashboard));
                return;
            }

            RefreshDashboard();
        }

        // ============================================================
        // 窗体关闭兜底
        // ============================================================

        /// <summary>
        /// 关闭确认：喷码服务运行中直接退出会中断生产，先问一句
        /// </summary>
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_printService.IsRunning && e.CloseReason == CloseReason.UserClosing)
            {
                if (!MessageHelper.ShowConfirm("喷码服务正在运行中，关闭程序将自动结束喷码。\r\n\r\n"
                                               + "已写入喷码机的码保持「已喷」状态，不会重发。\r\n"
                                               + "确认退出程序吗？"))
                {
                    e.Cancel = true;
                }
            }
        }

        /// <summary>
        /// 关闭兜底：同步执行停止流程，确保线程全退、连接全关、对象全释放（万总硬性要求）
        /// </summary>
        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                _printService.StopSync();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("退出时停止喷码服务失败", ex);
            }
        }

        // ============================================================
        // 配置入口（阶段二第 1 轮）
        // ============================================================

        /// <summary>
        /// 「喷码机配置」按钮 —— 打开连接配置窗体（TCP/串口二选一，双行保存）
        /// 【规约】ShowDialog 打开、using 用完销毁。
        /// </summary>
        private void btnPrintConfig_Click(object sender, EventArgs e)
        {
            try
            {
                using (PrinterConfigForm form = new PrinterConfigForm())
                {
                    form.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("打开喷码机配置窗体失败", ex);
                MessageHelper.ShowError("打开喷码机配置失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 「系统参数」按钮 —— 打开系统参数配置窗体（14 项可调参数，保存即生效）
        /// 【规约】ShowDialog 打开、using 用完销毁。
        /// </summary>
        private void btnSystemConfig_Click(object sender, EventArgs e)
        {
            try
            {
                using (SystemConfigForm form = new SystemConfigForm())
                {
                    form.ShowDialog(this);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("打开系统参数配置窗体失败", ex);
                MessageHelper.ShowError("打开系统参数配置失败：" + ex.Message);
            }
        }

        // ============================================================
        // 看板刷新
        // ============================================================

        /// <summary>
        /// 刷新生产看板数字
        /// 【数据来源】有机码可用数量：
        ///   运行中且基数有效 → 基数 − 发码数量（内存相减，运行期间零查库，D1 万总 2026-09-11 指令）；
        ///   其余情况（未启动/停止后/基数未知）→ 实时查库（未喷状态条数）；
        ///   发码数量 / 喷印数 = 本次运行计数，直接读 BLL（万总 20:23 指令，不再查库累计）。
        /// 【边界】查库失败不能让主界面起不来 —— 捕获后保持上一次的显示值，只记日志。
        /// </summary>
        private void RefreshDashboard()
        {
            try
            {
                // [2026-09-11] 看板与查库解耦（D1）：运行中且基数 ≥ 0 时走内存相减。
                //   依据：SendOneCode 里 MarkPrinted 成功必伴随发码数量 +1（失败则两边都不动），
                //   未喷库存的减少量 ≡ 发码数量，相减即库中真实未喷数（万总确认：不用管发码是否成功）。
                //   Math.Max(0, ...) 纯兜底防意外出现负数显示（规约：控件不得出现异常值）。
                //   未启动 / 停止后 / 基数未知（−1）→ 回退实时查库，原逻辑不动。
                int availableCount;
                if (_printService != null && _printService.IsRunning
                    && _printService.AvailableBase >= 0)
                {
                    availableCount = Math.Max(0,
                        _printService.AvailableBase - _printService.RunSendCount);
                }
                else
                {
                    availableCount = CodeDataDAL.GetStatusCount(PrintStatus.NotPrinted);
                }

                lblAvailableCount.Text = availableCount.ToString();

                // [2026-09-10] 万总 20:23 指令：下面两个计数改为"本次运行"口径（Start 时归零），
                //   累加由 BLL 负责，这里只负责读出来显示：
                //   · 发码数量 = 走到 Write 就 +1（无论写入成败、不看机器反馈）
                //   · 喷印数   = 只认机器回传的 0x32
                //   两者的差值即"发出去但机器还没喷出来"的在途量（万总：有数据差不要紧）。
                lblSendCount.Text = _printService.RunSendCount.ToString();
                lblPrintedCount.Text = _printService.RunPrintedCount.ToString();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("刷新生产看板失败", ex);
            }
        }

      
    }
}

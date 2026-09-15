using System.ComponentModel;
using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] 导入进度窗体 —— 后台导入 + 进度条 + 可取消
    ///
    /// 【为什么单独做一个窗体】单次导入 10 万行左右，若在 UI 线程同步执行，
    ///   界面会假死（转圈、无响应），用户无法取消也不知道进度。改成后台线程 + 模态进度框，
    ///   导入期间主界面保持响应，进度实时可见，可随时取消。
    ///
    /// 【为什么用 BackgroundWorker 而不是 Task】
    ///   BackgroundWorker 是 WinForms 原生方案：ReportProgress / RunWorkerCompleted
    ///   会自动封送回 UI 线程，不用自己写 Invoke，传统直白、不容易出错。
    ///
    /// 【取消语义（重要）】
    ///   点取消只是"发出取消信号"，后台线程检查到信号后会中止当前步骤，
    ///   由于写库在一个事务里，取消 = 事务回滚 = 一条都没入库，绝不会出现半截数据。
    ///   取消期间窗体不关闭，等后台线程真正收尾（RunWorkerCompleted）后才关，
    ///   避免出现"窗体关了但线程还在写库"的失控状态。
    ///
    /// 【关闭按钮处理】导入期间点右上角 X 不直接关窗，而是触发一次取消，
    ///   由后台线程安全收尾后自动关闭，防止用户以为关掉了其实还在写数据。
    ///
    /// 【使用方式】
    ///   using (ImportProgressForm form = new ImportProgressForm(filePath))
    ///   {
    ///       form.ShowDialog(this);
    ///       ImportResult result = form.Result;   // 可能为 null（极端异常）
    ///   }
    ///   本窗体不负责弹结果框 —— 结果交给调用方展示，保持职责单一。
    /// </summary>
    public class ImportProgressForm : Form
    {
        // ============================================================
        // 字段
        // ============================================================

        private readonly string _filePath = string.Empty;
        private readonly CancellationTokenSource _cancelSource = new CancellationTokenSource();

        private BackgroundWorker? _worker = null;
        private ImportResult? _result = null;

        /// <summary>标记：是否为"正常收尾后自动关闭"（用于区分用户点 X 与线程结束）</summary>
        private bool _completed = false;

        private Label lblMessage = new Label();
        private ProgressBar progressBar = new ProgressBar();
        private Button btnCancel = new Button();

        // ============================================================
        // 属性
        // ============================================================

        /// <summary>
        /// 导入结果。窗体关闭后读取；极端异常（后台线程抛出未捕获异常）时可能为 null
        /// </summary>
        public ImportResult? Result
        {
            get { return _result; }
        }

        // ============================================================
        // 构造与布局
        // ============================================================

        /// <summary>
        /// 构造导入进度窗体
        /// </summary>
        /// <param name="filePath">待导入文件完整路径</param>
        public ImportProgressForm(string filePath)
        {
            _filePath = filePath ?? string.Empty;

            InitializeComponent();
        }

        /// <summary>
        /// 界面布局
        /// 【说明】控件全部手写创建，不使用设计器生成代码 ——
        ///   本窗体控件少、结构简单，集中在一个文件里更容易看清整体，也避免设计器代码与逻辑分离带来的维护成本。
        /// </summary>
        private void InitializeComponent()
        {
            this.Text = "正在导入";
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.ClientSize = new Size(520, 180);
            this.Font = new Font("Microsoft YaHei UI", 10F);

            lblMessage.Name = "lblMessage";
            lblMessage.Text = "准备导入...";
            lblMessage.Location = new Point(20, 25);
            lblMessage.Size = new Size(480, 30);
            lblMessage.AutoEllipsis = true;

            progressBar.Name = "progressBar";
            progressBar.Location = new Point(20, 70);
            progressBar.Size = new Size(480, 30);
            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Value = 0;
            progressBar.Style = ProgressBarStyle.Continuous;

            btnCancel.Name = "btnCancel";
            btnCancel.Text = "取消导入";
            btnCancel.Location = new Point(200, 125);
            btnCancel.Size = new Size(120, 35);
            btnCancel.Click += new EventHandler(btnCancel_Click);

            this.Controls.Add(lblMessage);
            this.Controls.Add(progressBar);
            this.Controls.Add(btnCancel);

            this.Shown += new EventHandler(ImportProgressForm_Shown);
            this.FormClosing += new FormClosingEventHandler(ImportProgressForm_FormClosing);
        }

        // ============================================================
        // 事件
        // ============================================================

        /// <summary>窗体显示后立即启动后台导入</summary>
        private void ImportProgressForm_Shown(object sender, EventArgs e)
        {
            StartImport();
        }

        /// <summary>
        /// 取消按钮 —— 只发信号，不直接关窗
        /// 【边界】按钮点一次后就禁用，防止连点；真正的关闭由 RunWorkerCompleted 完成。
        /// </summary>
        private void btnCancel_Click(object sender, EventArgs e)
        {
            RequestCancel();
        }

        /// <summary>
        /// 窗体关闭拦截 —— 导入进行中的关闭请求一律转成"取消"
        /// </summary>
        private void ImportProgressForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_completed)
            {
                return;
            }

            if (_worker != null && _worker.IsBusy)
            {
                e.Cancel = true;
                RequestCancel();
            }
        }

        // ============================================================
        // 私有：启动与收尾
        // ============================================================

        /// <summary>创建并启动后台导入线程</summary>
        private void StartImport()
        {
            _worker = new BackgroundWorker();
            _worker.WorkerReportsProgress = true;
            _worker.WorkerSupportsCancellation = false;
            _worker.DoWork += new DoWorkEventHandler(Worker_DoWork);
            _worker.ProgressChanged += new ProgressChangedEventHandler(Worker_ProgressChanged);
            _worker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(Worker_RunWorkerCompleted);

            _worker.RunWorkerAsync();
        }

        /// <summary>后台线程：执行导入</summary>
        private void Worker_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = (BackgroundWorker)sender;

            ImportResult result = ImportBLL.ImportFromFile(_filePath, _cancelSource.Token,
                delegate (int percent, string message)
                {
                    worker.ReportProgress(percent, message);
                });

            e.Result = result;
        }

        /// <summary>UI 线程：刷新进度条与提示文字</summary>
        private void Worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            int percent = e.ProgressPercentage;
            if (percent < 0)
            {
                percent = 0;
            }
            if (percent > 100)
            {
                percent = 100;
            }
            progressBar.Value = percent;

            string message = string.Empty;
            if (e.UserState != null)
            {
                message = e.UserState.ToString() ?? string.Empty;
            }

            if (message.Length > 0)
            {
                lblMessage.Text = message;
            }
        }

        /// <summary>
        /// UI 线程：导入收尾
        /// 【三态处理】
        ///   正常完成 → _result 为 ImportBLL 返回值；
        ///   后台抛异常 → e.Error 非空，这里补一个失败的 ImportResult（并记日志），保证调用方拿不到 null；
        ///   用户取消   → ImportBLL 自己返回 Canceled 结果，走正常分支。
        /// </summary>
        private void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                LogHelper.Instance.Error("导入过程发生未处理异常", e.Error);

                ImportResult failed = new ImportResult();
                failed.Success = false;
                failed.Canceled = false;
                failed.Message = "导入过程异常终止：" + e.Error.Message;
                _result = failed;
            }
            else
            {
                _result = e.Result as ImportResult;
            }

            _completed = true;
            this.Close();
        }

        // ============================================================
        // 资源释放
        // ============================================================

        /// <summary>
        /// 窗体销毁时释放取消令牌源
        /// 【说明】CancellationTokenSource 持有可释放资源，必须显式释放；
        ///   调用方用 using 包住本窗体，Close 后即触发这里。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _cancelSource.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("释放取消令牌源失败：" + ex.Message);
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// 发出取消请求
        /// </summary>
        private void RequestCancel()
        {
            try
            {
                btnCancel.Enabled = false;
                lblMessage.Text = "正在取消，请稍候（已导入的数据将全部回滚）...";
                _cancelSource.Cancel();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("发起取消导入失败", ex);
            }
        }
    }
}

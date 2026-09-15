using System.ComponentModel;
using InkPrinterCode.BLL;
using InkPrinterCode.Common;
using InkPrinterCode.Model;

namespace InkPrinterCode.Forms
{
    /// <summary>
    /// [2026-09-10] Import progress form -- background import + progress bar + cancellable
    ///
    /// [Why a separate form] A single import is about 100,000 rows; executing it synchronously on the UI thread
    ///   would freeze the UI (spinning, unresponsive) and the user could neither cancel nor see progress.
    ///   A background thread plus a modal progress box keeps the main form responsive, shows progress in real
    ///   time and allows cancelling at any moment.
    ///
    /// [Why BackgroundWorker instead of Task]
    ///   BackgroundWorker is the native WinForms approach: ReportProgress / RunWorkerCompleted
    ///   automatically marshal back to the UI thread, so no manual Invoke is needed; it is straightforward and less error-prone.
    ///
    /// [Cancellation semantics (important)]
    ///   Clicking cancel only "raises a cancel signal"; the background thread detects it and aborts the current
    ///   step. Because writing to the DB happens in one transaction, cancel = rollback = nothing was committed,
    ///   so half-written data can never occur. The form does not close during cancellation; it waits until the
    ///   background thread truly finishes (RunWorkerCompleted) so there is no runaway state where "the form is
    ///   closed but the thread is still writing".
    ///
    /// [Close button handling] Clicking the X in the top-right during import does not close the form directly;
    ///   it triggers a cancellation, and the form closes automatically once the background thread has finished
    ///   safely. This prevents users from thinking it is closed while data is still being written.
    ///
    /// [Usage]
    ///   using (ImportProgressForm form = new ImportProgressForm(filePath))
    ///   {
    ///       form.ShowDialog(this);
    ///       ImportResult result = form.Result;   // may be null (extreme exception)
    ///   }
    ///   This form does not show the result box -- the result is left to the caller, keeping responsibilities single.
    /// </summary>
    public class ImportProgressForm : Form
    {
        // ============================================================
        // Fields
        // ============================================================

        private readonly string _filePath = string.Empty;
        private readonly CancellationTokenSource _cancelSource = new CancellationTokenSource();

        private BackgroundWorker? _worker = null;
        private ImportResult? _result = null;

        /// <summary>Flag: whether this is "auto-close after a normal finish" (distinguishes the user clicking X from the thread completing)</summary>
        private bool _completed = false;

        private Label lblMessage = new Label();
        private ProgressBar progressBar = new ProgressBar();
        private Button btnCancel = new Button();

        // ============================================================
        // Properties
        // ============================================================

        /// <summary>
        /// Import result. Read after the form closes; may be null in extreme cases (an unhandled exception thrown on the background thread)
        /// </summary>
        public ImportResult? Result
        {
            get { return _result; }
        }

        // ============================================================
        // Construction and layout
        // ============================================================

        /// <summary>
        /// Construct the import progress form
        /// </summary>
        /// <param name="filePath">Full path of the file to import</param>
        public ImportProgressForm(string filePath)
        {
            _filePath = filePath ?? string.Empty;

            InitializeComponent();
        }

        /// <summary>
        /// UI layout
        /// [Note] All controls are created by hand rather than with designer-generated code --
        ///   this form has few controls and a simple structure, so keeping everything in one file makes the whole
        ///   thing easier to follow and avoids the maintenance cost of splitting designer code from logic.
        /// </summary>
        private void InitializeComponent()
        {
            this.Text = "Importing";
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.ClientSize = new Size(520, 180);
            this.Font = new Font("Microsoft YaHei UI", 10F);

            lblMessage.Name = "lblMessage";
            lblMessage.Text = "Preparing to import...";
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
            btnCancel.Text = "Cancel Import";
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
        // Events
        // ============================================================

        /// <summary>Start the background import as soon as the form is shown</summary>
        private void ImportProgressForm_Shown(object sender, EventArgs e)
        {
            StartImport();
        }

        /// <summary>
        /// Cancel button -- only raises the signal, does not close the form directly
        /// [Boundary] The button is disabled after one click to prevent repeated clicks; the actual close is done by RunWorkerCompleted.
        /// </summary>
        private void btnCancel_Click(object sender, EventArgs e)
        {
            RequestCancel();
        }

        /// <summary>
        /// Form close interception -- any close request while the import is running is converted into a "cancel"
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
        // Private: start and finish
        // ============================================================

        /// <summary>Create and start the background import thread</summary>
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

        /// <summary>Background thread: perform the import</summary>
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

        /// <summary>UI thread: refresh the progress bar and the message label</summary>
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
        /// UI thread: import finish
        /// [Three states]
        ///   Normal completion -> _result is ImportBLL's return value;
        ///   Background exception -> e.Error is non-null; a failed ImportResult is synthesized here (and logged)
        ///   so the caller never gets null;
        ///   User cancellation -> ImportBLL itself returns a Canceled result, so the normal branch handles it.
        /// </summary>
        private void Worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                LogHelper.Instance.Error("An unhandled exception occurred during import", e.Error);

                ImportResult failed = new ImportResult();
                failed.Success = false;
                failed.Canceled = false;
                failed.Message = "Import terminated abnormally: " + e.Error.Message;
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
        // Resource release
        // ============================================================

        /// <summary>
        /// Release the cancellation token source when the form is disposed
        /// [Note] CancellationTokenSource holds disposable resources and must be released explicitly;
        ///   the caller wraps this form in using, so this runs right after Close.
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
                    System.Diagnostics.Debug.WriteLine("Failed to release the cancellation token source: " + ex.Message);
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Raise a cancellation request
        /// </summary>
        private void RequestCancel()
        {
            try
            {
                btnCancel.Enabled = false;
                lblMessage.Text = "Cancelling, please wait (all imported data will be rolled back)...";
                _cancelSource.Cancel();
            }
            catch (Exception ex)
            {
                LogHelper.Instance.Error("Failed to request import cancellation", ex);
            }
        }
    }
}

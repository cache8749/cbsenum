// Translation of CBSEnum_JobProcessor.pas

namespace CBSEnum;

/// <summary>
/// Modal-ish window that runs a <see cref="ProcessingThread"/> and streams its log output.
/// </summary>
public sealed class JobProcessorForm : Form
{
    // -------------------------------------------------------------------------
    // Controls
    // -------------------------------------------------------------------------
    private readonly RichTextBox _log;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly Button _btnClose;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private ProcessingThread? _thread;
    private readonly object _logLock = new();
    private readonly List<string> _pendingLines = new();

    // -------------------------------------------------------------------------
    // Constructor / InitializeComponent
    // -------------------------------------------------------------------------
    public JobProcessorForm()
    {
        Text            = "Processing...";
        Size            = new Size(640, 400);
        MinimumSize     = new Size(400, 200);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.SizableToolWindow;

        _log = new RichTextBox
        {
            Dock      = DockStyle.Fill,
            ReadOnly  = true,
            Font      = new Font("Consolas", 9f),
            BackColor = SystemColors.Window,
            ScrollBars= RichTextBoxScrollBars.Vertical,
        };

        _btnClose = new Button
        {
            Text   = "Close",
            Dock   = DockStyle.Bottom,
            Height = 28,
        };
        _btnClose.Click += (_, _) => Close();

        Controls.Add(_log);
        Controls.Add(_btnClose);

        _updateTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _updateTimer.Tick += OnTimerTick;

        FormClosing += OnFormClosing;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public void Log(string message)
    {
        if (InvokeRequired)
            BeginInvoke(() => AppendLog(message));
        else
            AppendLog(message);
    }

    /// <summary>Starts the job and shows the form non-modally (caller should Show() then call this).</summary>
    public void Process(ProcessingThread job)
    {
        if (_thread is not null)
            throw new InvalidOperationException("Another operation is still in progress.");

        _thread = job;
        _thread.OnLog = ThreadLog;
        _thread.Start();
        _updateTimer.Start();
    }

    public void EndProcessing()
    {
        if (_thread is null) return;
        _thread.RequestTermination();
        _thread.WaitFor();
        _thread = null;
        _updateTimer.Stop();
        FlushPendingLog();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void AppendLog(string message)
    {
        _log.AppendText(message + Environment.NewLine);
        _log.ScrollToCaret();
    }

    // Called from background thread – must be thread-safe
    private void ThreadLog(string message)
    {
        lock (_logLock) _pendingLines.Add(message);
    }

    private void FlushPendingLog()
    {
        string[] lines;
        lock (_logLock)
        {
            if (_pendingLines.Count == 0) return;
            lines = _pendingLines.ToArray();
            _pendingLines.Clear();
        }
        foreach (var l in lines) AppendLog(l);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        FlushPendingLog();

        if (_thread is null) { _updateTimer.Stop(); return; }

        if (_thread.FatalException is { } ex)
        {
            AppendLog($"Fatal exception {ex.GetType().Name}: {ex.Message}");
            EndProcessing();
            return;
        }

        if (_thread.IsFinished) EndProcessing();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_thread is null) return;
        if (MessageBox.Show("The operation is still in progress. Abort it?",
                "Confirm abort", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        EndProcessing();
    }
}

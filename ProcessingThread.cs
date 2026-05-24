// Translation of TProcessingThread base class from CBSEnum_JobProcessor.pas

namespace CBSEnum;

/// <summary>Base class for background jobs run by <see cref="JobProcessorForm"/>.</summary>
public abstract class ProcessingThread
{
    private Action<string>? _onLog;

    public Action<string>? OnLog
    {
        get => _onLog;
        set => _onLog = value;
    }

    // Set by the runner when it wants this job to stop early
    protected volatile bool _terminated;
    public bool Terminated => _terminated;

    public void RequestTermination() => _terminated = true;

    public Exception? FatalException { get; private set; }

    // The Thread object wrapping this job
    private Thread? _thread;

    public void Start()
    {
        _thread = new Thread(RunSafe) { IsBackground = true };
        _thread.Start();
    }

    public void WaitFor() => _thread?.Join();

    public bool IsFinished => _thread is { IsAlive: false };

    private void RunSafe()
    {
        try
        {
            Execute();
        }
        catch (Exception ex)
        {
            FatalException = ex;
        }
    }

    protected abstract void Execute();

    protected void Log(string message) => _onLog?.Invoke(message);
}

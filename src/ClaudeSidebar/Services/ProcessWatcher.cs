using System.Diagnostics;
using System.Windows.Threading;

namespace ClaudeSidebar;

// Claude Code 데스크톱 앱(claude.exe) 실행 여부를 3초 간격으로 감시한다.
public class ProcessWatcher
{
    private readonly DispatcherTimer _timer;

    public bool IsClaudeRunning { get; private set; }
    public event Action<bool>? RunningChanged;

    public ProcessWatcher()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Check();
    }

    public void Start()
    {
        Check();
        _timer.Start();
    }

    private void Check()
    {
        bool running = false;
        try
        {
            var procs = Process.GetProcessesByName("claude");
            running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
        }
        catch { }

        if (running != IsClaudeRunning)
        {
            IsClaudeRunning = running;
            Log.Write("claude running: " + running);
            RunningChanged?.Invoke(running);
        }
    }
}

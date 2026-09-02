using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ClaudeSidebar;

// Claude Code 데스크톱 앱(claude.exe) 실행 여부를 3초 간격으로 감시한다.
// 어느 모니터에 떠 있는지도 함께 본다 — 사이드바가 그 화면으로 따라가기 위해서다.
public class ProcessWatcher
{
    private readonly DispatcherTimer _timer;

    public bool IsClaudeRunning { get; private set; }

    /// Claude 창이 떠 있는 모니터의 장치 이름. 쓸 만한 창을 못 찾으면 마지막 값을 유지한다
    /// (최소화하거나 잠깐 다른 앱을 쓴다고 사이드바가 되돌아가면 안 된다).
    public string? ClaudeMonitor { get; private set; }

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

            var pids = new HashSet<int>();
            IntPtr anyMain = IntPtr.Zero;
            foreach (var p in procs)
            {
                pids.Add(p.Id);
                if (anyMain == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) anyMain = p.MainWindowHandle;
                p.Dispose();
            }
            UpdateMonitor(PickClaudeWindow(pids, anyMain));
        }
        catch { }

        if (running != IsClaudeRunning)
        {
            IsClaudeRunning = running;
            Log.Write("claude running: " + running);
            RunningChanged?.Invoke(running);
        }
    }

    // 포커스가 Claude 에 있으면 그 창을, 아니면 아무 Claude 본창이나 고른다.
    // 최소화된 창은 좌표가 (-32000,-32000) 이라 모니터 판정이 엉뚱해진다 — 걸러낸다.
    private static IntPtr PickClaudeWindow(HashSet<int> pids, IntPtr fallback)
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && Usable(fg))
        {
            GetWindowThreadProcessId(fg, out int pid);
            if (pids.Contains(pid)) return fg;
        }
        return Usable(fallback) ? fallback : IntPtr.Zero;
    }

    private static bool Usable(IntPtr h) => h != IntPtr.Zero && IsWindowVisible(h) && !IsIconic(h);

    private void UpdateMonitor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var name = DisplayInfo.ForWindow(hwnd).Name;
        if (name == ClaudeMonitor) return;
        ClaudeMonitor = name;
        Log.Write($"[follow] Claude 창 모니터 = {name}");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}

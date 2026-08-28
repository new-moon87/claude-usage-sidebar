using System.Net.Http;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeSidebar;

public partial class App : Application
{
    private Mutex? _mutex;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private ProcessWatcher? _watcher;
    private CredentialStore? _creds;
    private UsageApiClient? _api;
    private readonly SettingsStore _settings = new();
    private readonly UsageHistoryReader _history = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private DispatcherTimer? _usageTimer;
    private DispatcherTimer? _tickTimer;
    private DispatcherTimer? _guardTimer;
    private readonly List<DispatcherTimer> _reprobes = new();
    private string _lastFingerprint = "";
    private UsageSnapshot? _last;
    private string? _status;
    private bool _fetching;
    private DateTimeOffset _lastManual = DateTimeOffset.MinValue;
    private DateTimeOffset _usageBlockedUntil = DateTimeOffset.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "ClaudeSidebar_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        Log.Write("=== app start ===");
        Log.Write($"[build] {(System.Diagnostics.Debugger.IsAttached ? "debug-attached" : "no-debugger")} " +
                  $"exe={Environment.ProcessPath}");
        DisplayInfo.LogAll("start");
        _settings.Load();
        _creds = new CredentialStore(_http);
        _api = new UsageApiClient(_http);

        _window = new MainWindow { Pinned = _settings.Settings.Pinned };
        // 구버전 설정(주 모니터 기준 DIP Top)은 물리 Y 로 한 번 옮겨 준다.
        int? physY = _settings.Settings.PhysY
                     ?? (_settings.Settings.Top is double t ? (int)Math.Round(t) : null);
        _window.SetSavedPlacement(_settings.Settings.MonitorName, physY);
        _window.RefreshRequested += ManualRefresh;
        _window.ReloginRequested += OpenLoginTerminal;
        _window.PinnedChanged += p => { _settings.Settings.Pinned = p; _settings.Save(); };
        _window.PlacementChanged += (mon, y) =>
        {
            _settings.Settings.MonitorName = mon;
            _settings.Settings.PhysY = y;
            _settings.Settings.Top = null;   // 구버전 값은 폐기
            _settings.Save();
        };

        _tray = new TrayIcon(_settings.Settings.ForceShow, _settings.Settings.Autostart);
        _tray.RefreshRequested += ManualRefresh;
        _tray.ReloginRequested += OpenLoginTerminal;
        _tray.ForceShowChanged += v =>
        {
            _settings.Settings.ForceShow = v;
            _settings.Save();
            UpdateVisibility();
        };
        _tray.AutostartChanged += v =>
        {
            _settings.Settings.Autostart = v;
            _settings.Save();
            Autostart.Set(v);
        };
        _tray.ExitRequested += () =>
        {
            _tray?.Dispose();
            _tray = null;
            Shutdown();
        };

        if (_settings.Settings.Autostart) Autostart.Set(true);

        _watcher = new ProcessWatcher();
        _watcher.RunningChanged += _ => Dispatcher.Invoke(UpdateVisibility);

        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _usageTimer.Tick += (_, _) => _ = RefreshAsync();

        // 카운트다운 텍스트는 30초마다 다시 계산한다.
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tickTimer.Tick += (_, _) => _window?.ApplySnapshot(_last, _status);

        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, pe) =>
        {
            if (pe.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                Dispatcher.Invoke(() =>
                {
                    _ = RefreshAsync(force: true);
                    OnDisplayChanged("resume");
                });
            }
        };

        // 모니터 연결/해제/배율 변경. 이 신호는 구성이 확정되기 전에 오므로 한 번만 반응하면 안 된다.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            Dispatcher.Invoke(() => OnDisplayChanged("event"));

        // 모니터 전원을 껐다 켜는 경로는 이벤트가 아예 안 오기도 한다. 실제 좌표와 주기적으로 대조하는 그물.
        _lastFingerprint = DisplayInfo.Fingerprint();
        _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _guardTimer.Tick += (_, _) => GuardTick();
        _guardTimer.Start();

        // 첫 화면은 앱 기록 파일로 즉시 채우고, 곧바로 API로 갱신한다.
        _last = _history.ReadLast();
        _window.ApplySnapshot(_last, null);
        _watcher.Start();
        UpdateVisibility();
        _ = RefreshAsync(force: true);
        _tickTimer.Start();
    }

    // 디스플레이 구성이 흔들릴 때 여러 시점에서 다시 확인한다. 마지막 값이 최종이다.
    private static readonly double[] ReprobeDelays = { 0.15, 0.7, 2.0, 5.0 };

    private void OnDisplayChanged(string source)
    {
        Log.Write($"[display] 변경 신호 수신({source}) — {string.Join("/", ReprobeDelays)}초 뒤 재확인 예약");
        DisplayInfo.LogAll(source);

        foreach (var t in _reprobes) t.Stop();
        _reprobes.Clear();

        foreach (var delay in ReprobeDelays)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _reprobes.Remove(timer);
                _lastFingerprint = DisplayInfo.Fingerprint();
                if (_window is { IsVisible: true })
                    _window.ApplyPlacement($"{source}+{delay}s");
            };
            _reprobes.Add(timer);
            timer.Start();
        }
    }

    private void GuardTick()
    {
        var fp = DisplayInfo.Fingerprint();
        if (fp != _lastFingerprint)
        {
            _lastFingerprint = fp;
            Log.Write("[display] 주기 점검에서 구성 변경 감지 (이벤트 미수신 경로)");
            OnDisplayChanged("polled");
            return;
        }
        if (_window?.IsPlacementDrifted() == true)
        {
            Log.Write("[display] 주기 점검에서 위치 이탈 감지 — 재배치");
            _window.ApplyPlacement("guard");
        }
    }

    private void UpdateVisibility()
    {
        bool show = _settings.Settings.ForceShow || (_watcher?.IsClaudeRunning ?? false);
        if (show)
        {
            _window?.ShowSidebar();
            _usageTimer?.Start();
        }
        else
        {
            _window?.HideSidebar();
            _usageTimer?.Stop();
        }
    }

    // Claude Code CLI 실행 파일을 찾는다: PATH → 데스크톱 앱 내장 엔진 → 네이티브 설치 경로 순.
    private static string? FindClaudeCli()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                foreach (var name in new[] { "claude.cmd", "claude.exe", "claude.bat" })
                {
                    var p = System.IO.Path.Combine(dir.Trim(), name);
                    if (System.IO.File.Exists(p)) return p;
                }
            }
            catch { }
        }
        try
        {
            var baseDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code");
            if (System.IO.Directory.Exists(baseDir))
            {
                var exe = new System.IO.DirectoryInfo(baseDir).GetDirectories()
                    .OrderByDescending(d => d.LastWriteTimeUtc)
                    .Select(d => System.IO.Path.Combine(d.FullName, "claude.exe"))
                    .FirstOrDefault(System.IO.File.Exists);
                if (exe is not null) return exe;
            }
        }
        catch { }
        var native = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return System.IO.File.Exists(native) ? native : null;
    }

    // cmd 창을 열어 Claude Code 로그인 절차를 시작한다.
    // /login 인자가 안 먹는 버전이어도 로그아웃 상태면 claude가 알아서 로그인 화면을 띄운다.
    private void OpenLoginTerminal()
    {
        try
        {
            var cli = FindClaudeCli();
            if (cli is null)
            {
                MessageBox.Show(
                    "Claude Code를 찾을 수 없습니다.\nClaude Code CLI 또는 Claude Code 데스크톱 앱을 먼저 설치해 주세요.",
                    "Claude 사이드바", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k title Claude 로그인 & echo 로그인 화면이 안 나오면 /login 을 입력해 주세요. & \"" + cli + "\" /login",
                UseShellExecute = true
            });
            Log.Write("login terminal opened: " + cli);
        }
        catch (Exception ex)
        {
            Log.Write("login terminal error: " + ex.Message);
        }
    }

    private void ManualRefresh()
    {
        if (DateTimeOffset.Now - _lastManual < TimeSpan.FromSeconds(5))
        {
            _window?.FlashThrottled();
            return;
        }
        _lastManual = DateTimeOffset.Now;
        _window?.ShowRefreshing();
        _ = RefreshAsync(force: true);
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_fetching || _creds is null || _api is null) return;
        if (!force && !(_settings.Settings.ForceShow || (_watcher?.IsClaudeRunning ?? false))) return;
        if (DateTimeOffset.Now < _usageBlockedUntil)
        {
            _window?.ApplySnapshot(_last, _status);
            return;
        }
        _fetching = true;
        try
        {
            UsageSnapshot? snap = null;
            System.Net.HttpStatusCode code = 0;
            var token = await _creds.GetAccessTokenAsync();
            if (token is not null)
            {
                (snap, code) = await _api.FetchAsync(token);
                if (snap is null && (int)code == 401)
                {
                    token = await _creds.GetAccessTokenAsync(forceRefresh: true);
                    if (token is not null) (snap, code) = await _api.FetchAsync(token);
                }
            }

            if (snap is not null)
            {
                _last = snap;
                _status = null;
            }
            else
            {
                var file = _history.ReadLast();
                if (file is not null && (_last is null || _last.Source == "FILE"))
                    _last = file;
                if (token is null)
                    _status = _creds.LastError ?? "재로그인 필요";
                else if ((int)code == 429)
                {
                    // 엔드포인트 레이트리밋: 3분간 폴링을 멈춰 페널티가 풀리게 한다
                    _usageBlockedUntil = DateTimeOffset.Now.AddMinutes(3);
                    _status = "요청이 너무 잦음 · 잠시 후 자동 갱신";
                }
                else
                    _status = $"API 오류 (HTTP {(int)code})";
            }
        }
        catch (Exception ex)
        {
            _status = "오류";
            Log.Write("refresh error: " + ex.Message);
        }
        finally
        {
            _fetching = false;
            _window?.ApplySnapshot(_last, _status);
            Log.Write($"refresh done: src={_last?.Source} H={_last?.FiveHour?.Utilization} " +
                      $"W={_last?.SevenDay?.Utilization} F={_last?.ModelWeekly?.Utilization} " +
                      $"C={_last?.ExtraUsagePct} status={_status}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}

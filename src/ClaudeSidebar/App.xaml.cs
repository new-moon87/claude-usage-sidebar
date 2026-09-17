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
    private readonly CodexHistoryReader _codexHistory = new();
    private readonly SettingsStore _settings = new();
    private readonly UsageHistoryReader _history = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private DispatcherTimer? _usageTimer;
    private DispatcherTimer? _tickTimer;
    private DispatcherTimer? _guardTimer;
    private readonly List<DispatcherTimer> _reprobes = new();
    private string _lastFingerprint = "";
    private UsageSnapshot? _last;
    private CodexSnapshot? _lastCodex;
    private string? _status;
    private string? _codexStatus;
    private bool _fetching;
    private DateTimeOffset _lastManual = DateTimeOffset.MinValue;
    private DateTimeOffset _usageBlockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _codexLastApi = DateTimeOffset.MinValue;
    private bool _codexPresent;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 진단 모드는 상주하지 않는다. 뮤텍스도 잡지 않으므로 앱이 떠 있어도 돌릴 수 있다.
        if (e.Args.Length >= 2 && e.Args[0] == "--menu-preview")
        {
            base.OnStartup(e);
            MenuPreview.Render(e.Args[1], Shutdown);
            return;
        }

        _mutex = new Mutex(true, "ClaudeSidebar_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
        Log.Write("=== app start ===");
        Log.Write($"[build] v{UpdateChecker.CurrentVersion} " +
                  $"{(System.Diagnostics.Debugger.IsAttached ? "debug-attached" : "no-debugger")} " +
                  $"exe={Environment.ProcessPath}");
        DisplayInfo.LogAll("start");
        _settings.Load();
        _creds = new CredentialStore(_http);
        _api = new UsageApiClient(_http);

        _window = new MainWindow { Pinned = _settings.Settings.Pinned };
        _codexPresent = CodexAppServer.IsInstalled();
        _window.SetCodexVisible(_codexPresent);
        Log.Write("[codex] 설치 감지: " + (_codexPresent ? "있음" : "없음 — 구역 숨김"));
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
        // 표시 전에 따라갈 모니터를 먼저 잡는다 — 순서가 뒤바뀌면 기본 모니터에 한 번 떴다가 건너뛴다.
        _watcher.RunningChanged += _ => Dispatcher.Invoke(() =>
        {
            _window?.FollowClaude(_watcher?.ClaudeMonitor);
            UpdateVisibility();
        });

        _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _usageTimer.Tick += (_, _) => _ = RefreshAsync();

        // 카운트다운 텍스트는 30초마다 다시 계산한다.
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tickTimer.Tick += (_, _) => _window?.ApplySnapshot(_last, _lastCodex, StatusLine());

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
        if (_codexPresent) _lastCodex = _codexHistory.ReadLast();
        _window.ApplySnapshot(_last, _lastCodex, null);
        _watcher.Start();
        UpdateVisibility();
        _ = RefreshAsync(force: true);
        _tickTimer.Start();

        UpdateInstaller.CleanupPreviousVersion();
        _ = CheckUpdateAsync();
    }

    // 시작할 때만 갈아 끼운다. 쓰는 도중 재시작하면 사용자가 시킨 적 없는 일로 화면이 사라진다.
    private async Task CheckUpdateAsync()
    {
        var result = await new UpdateChecker(_http).CheckAsync();
        if (!result.IsUpdateAvailable) return;

        // 새 인스턴스가 단일 인스턴스 뮤텍스에 막혀 즉시 죽지 않도록 먼저 치운다.
        //
        // ReleaseMutex 만으로는 부족하다 — 그것은 소유권만 놓을 뿐이고, 이름 있는 뮤텍스는
        // 핸들이 전부 닫혀야 사라진다. 핸들을 쥔 채 새 exe 를 띄우면 그쪽은 createdNew=false 로
        // 보고 로그 한 줄 없이 죽는다(= 업데이트 후 앱이 사라진 것처럼 보인다).
        // 실제로 0.1.3.0 → 0.1.4.0 에서 이 경합에 걸렸다. 반드시 Dispose 까지 한다.
        //
        // ReleaseMutex 는 만든 스레드에서만 되므로 UI 스레드에서 호출해야 한다(여기가 그 스레드다).
        // 교체가 실패하면 이번 실행은 단일 인스턴스 보호 없이 계속되지만, 스스로 두 번 뜨는 경로는 없다.
        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _mutex = null;
        }
        catch (Exception ex) { Log.Write("[update] 뮤텍스 정리 실패: " + ex.GetType().Name); }

        if (await UpdateInstaller.ApplyAsync(result, _http)) Shutdown();
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
        _window?.FollowClaude(_watcher?.ClaudeMonitor);

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
            await RefreshCodexAsync(force);
            _window?.ApplySnapshot(_last, _lastCodex, StatusLine());
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
                    _status = Tag("Claude", _creds.LastError ?? "재로그인 필요");
                else if ((int)code == 429)
                {
                    // 엔드포인트 레이트리밋: 3분간 폴링을 멈춰 페널티가 풀리게 한다
                    _usageBlockedUntil = DateTimeOffset.Now.AddMinutes(3);
                    _status = "Claude · 요청이 너무 잦음 · 잠시 후 자동 갱신";
                }
                else
                    _status = $"Claude · API 오류 (HTTP {(int)code})";
            }

            await RefreshCodexAsync(force);
        }
        catch (Exception ex)
        {
            _status = "Claude · 오류";
            Log.Write("refresh error: " + ex.Message);
        }
        finally
        {
            _fetching = false;
            _window?.ApplySnapshot(_last, _lastCodex, StatusLine());
            Log.Write($"refresh done: src={_last?.Source} H={_last?.FiveHour?.Utilization} " +
                      $"W={_last?.SevenDay?.Utilization} F={_last?.ModelWeekly?.Utilization} " +
                      $"C={_last?.ExtraUsagePct} status={_status}");
        }
    }

    // Codex 사용량. Claude 와 완전히 독립이라 한쪽이 죽어도 다른 쪽은 그대로 보인다.
    //
    // 값은 Codex 자기 바이너리(app-server)에서 받는다. HTTP 로 직접 받는 길은 Windows 에서 막혀 있고,
    // rollout 기록만으로는 그 세션이 쓴 모델 한도만 보여 계정 전체가 0% 로 보인다(실측).
    // 프로세스를 띄우는 비용이 있으니 자주 부르지 않는다.
    private async Task RefreshCodexAsync(bool force)
    {
        if (!_codexPresent) return;

        var gap = force ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(10);
        if (DateTimeOffset.Now - _codexLastApi < gap)
        {
            var cached = _codexHistory.ReadLast();
            if (cached is not null && (_lastCodex is null || _lastCodex.Source == "FILE"))
                _lastCodex = cached;
            return;
        }
        _codexLastApi = DateTimeOffset.Now;

        try
        {
            var snap = await CodexAppServer.ReadAsync(TimeSpan.FromSeconds(30));
            if (snap is not null)
            {
                _lastCodex = snap;
                _codexStatus = null;
            }
            else
            {
                // app-server 가 안 되면 기록 파일로 내려간다. 이미 CLI 값을 들고 있으면 덮어쓰지 않는다.
                var file = _codexHistory.ReadLast();
                if (file is not null && (_lastCodex is null || _lastCodex.Source == "FILE"))
                    _lastCodex = file;
                _codexStatus = _lastCodex is null ? "Codex · 사용량을 못 읽음" : null;
            }
        }
        catch (Exception ex)
        {
            _codexStatus = null;
            Log.Write("[codex] refresh error: " + ex.Message);
        }
        Log.Write($"codex refresh done: src={_lastCodex?.Source} short={_lastCodex?.Short?.Percent}" +
                  $"({_lastCodex?.Short?.Label}) long={_lastCodex?.Long?.Percent}({_lastCodex?.Long?.Label}) " +
                  $"plan={_lastCodex?.PlanType} credit={_lastCodex?.CreditDetail} status={_codexStatus}");
    }

    // 제품명이 이미 들어 있는 메시지는 그대로 둔다(같은 말을 두 번 붙이지 않게).
    private static string Tag(string product, string message) =>
        message.Contains(product) ? message : product + " · " + message;

    private string? StatusLine()
    {
        if (string.IsNullOrEmpty(_status)) return _codexStatus;
        if (string.IsNullOrEmpty(_codexStatus)) return _status;
        return _status + Environment.NewLine + _codexStatus;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}

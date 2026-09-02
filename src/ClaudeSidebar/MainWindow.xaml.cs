using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClaudeSidebar;

public partial class MainWindow : Window
{
    private const double PillW = 13;
    private const double PillH = 64;
    private const double RowBarW = 176;
    // 창의 논리 크기(DIP). 이 값은 절대 SetWindowPos 로 바꾸지 않는다 — 함정 ⑪ 참고.
    private const double BaseW = 260;
    private const double BaseH = 320;
    private static readonly Color Red = Color.FromRgb(0xE2, 0x4B, 0x4A);
    private static readonly CultureInfo Korean = new("ko-KR");

    private class Pill
    {
        public Color BaseColor;
        public TextBlock LetterText = null!;
        public TextBlock Digits = null!;
        public Rectangle Fill = null!;
        public bool Pulsing;
        public TextBlock RowName = null!;
        public TextBlock RowPct = null!;
        public TextBlock RowReset = null!;
        public Rectangle RowFill = null!;
    }

    private readonly Pill _h;
    private readonly Pill _w;
    private readonly Pill _f;
    private readonly Pill _c;
    private readonly DispatcherTimer _collapseTimer;
    private TextBlock _footer = null!;
    private TextBlock _statusLine = null!;
    private TextBlock _pinBtn = null!;
    private string? _savedMonitor;
    private string? _followedMonitor;
    private int? _savedPhysY;
    private bool _pinned;
    private string _footerBase = "--";
    private DispatcherTimer? _flashTimer;

    public bool Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            if (_pinBtn is not null) UpdatePinVisual();
        }
    }
    public event Action? RefreshRequested;
    public event Action? ReloginRequested;
    public event Action<bool>? PinnedChanged;
    public event Action<string, int>? PlacementChanged;

    public MainWindow()
    {
        InitializeComponent();
        BuildHeader();
        _h = MakePill("H", "5시간 세션", Color.FromRgb(0x7F, 0x77, 0xDD));
        _w = MakePill("W", "주간 · 전체", Color.FromRgb(0xEF, 0x9F, 0x27));
        _f = MakePill("F", "주간 · Fable", Color.FromRgb(0x37, 0x8A, 0xDD));
        _c = MakePill("C", "추가 사용량", Color.FromRgb(0x5D, 0xCA, 0xA5));
        BuildFooter();

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Pinned && !RootPanel.IsMouseOver) Collapse();
        };

        RootPanel.MouseEnter += (_, _) => Expand();
        RootPanel.MouseLeave += (_, _) => { if (!Pinned) _collapseTimer.Start(); };
        PillStrip.MouseLeftButtonDown += OnStripMouseDown;
        Loaded += (_, _) => ApplyPlacement("loaded");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 도구 창 + 비활성화 창으로 만들어 Alt-Tab과 포커스 훔침을 방지한다.
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, ex | WsExToolwindow | WsExNoactivate);
    }

    public void SetSavedPlacement(string? monitor, int? physY)
    {
        _savedMonitor = monitor;
        _savedPhysY = physY;
    }

    // Claude 창이 다른 모니터로 옮겨 갔으면 사이드바도 그 화면으로 따라간다.
    // 같은 모니터면 아무 일도 하지 않는다 — 그래야 사용자가 끌어다 놓은 위치를 2초마다 빼앗지 않는다.
    public void FollowClaude(string? monitor)
    {
        if (monitor is null || monitor == _followedMonitor) return;
        _followedMonitor = monitor;
        if (monitor == _savedMonitor) return;

        Log.Write($"[follow] 사이드바 이동 {_savedMonitor ?? "(기본)"} → {monitor}");
        _savedMonitor = monitor;
        _savedPhysY = null;   // 다른 모니터라 세로 위치는 새로 잡는다(가운데)
        if (IsVisible) ApplyPlacement("follow");
    }

    // 목표 모니터를 고른다. 모니터 목록은 매번 새로 묻는다(캐시 금지 — 함정 ⑧).
    private DisplayInfo.Mon PickMonitor()
    {
        var mons = DisplayInfo.Enumerate();
        if (mons.Count == 0)
            return new DisplayInfo.Mon("(none)", default, default, 96, 96, true);
        if (_savedMonitor is not null)
        {
            var saved = mons.FirstOrDefault(m => m.Name == _savedMonitor);
            if (saved is not null) return saved;
        }
        return mons.FirstOrDefault(m => m.Primary) ?? mons[0];
    }

    // 물리 픽셀 기준으로 배치한다.
    // 크기는 건드리지 않는다(SWP_NOSIZE): DPI 경계를 넘을 때 WPF 가 물리값을 DIP 로 착각해
    // 창을 2배/절반으로 되돌리기 때문이다. 크기는 WPF 논리값(BaseW/BaseH)에 맡긴다.
    public void ApplyPlacement(string reason)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Log.Write($"[place:{reason}] hwnd 없음 — 건너뜀");
            return;
        }

        // 논리 크기가 어긋나 있으면 되돌린다(툴킷이 되돌린 흔적 복구).
        if (Math.Abs(Width - BaseW) > 0.5 || Math.Abs(Height - BaseH) > 0.5)
        {
            Log.Write($"[place:{reason}] 논리크기 이탈 {Width:0.#}x{Height:0.#} → {BaseW}x{BaseH} 복구");
            Width = BaseW;
            Height = BaseH;
            UpdateLayout();
        }

        var target = PickMonitor();

        // DPI 경계를 넘으면 물리 크기가 바뀌므로, 실제 크기를 다시 읽어 재계산한다.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (!DisplayInfo.GetWindowRect(hwnd, out var cur)) return;
            int pw = cur.Width, ph = cur.Height;

            // 가장자리는 반드시 실제 변 좌표로 계산한다(폭 × 비율 금지 — 함정 ⑨).
            int x = target.Work.Right - pw;
            int y = _savedPhysY ?? (target.Work.Top + (target.Work.Height - ph) / 2);
            y = Math.Max(target.Work.Top, Math.Min(y, target.Work.Bottom - ph));

            DisplayInfo.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, DisplayInfo.SwpMoveOnly);

            if (!DisplayInfo.GetWindowRect(hwnd, out var after)) return;
            var landed = DisplayInfo.ForWindow(hwnd);
            bool ok = after.Left == x && after.Top == y
                      && after.Width == pw && after.Height == ph
                      && landed.Name == target.Name;

            Log.Write($"[place:{reason}] 시도{attempt} 목표={target.Name} work={target.Work} dpi={target.DpiX} " +
                      $"| 지정=({x},{y}) {pw}x{ph} | 실제={after} | 착지={landed.Name} dpi={landed.DpiX} " +
                      $"| wpf={Width:0.#}x{Height:0.#} | {(ok ? "일치" : "불일치→재시도")}");

            if (ok) return;
            target = PickMonitor();
        }
    }

    // 2초 그물이 쓰는 대조. 목표 모니터의 오른쪽 변에 정확히 붙어 있는지 물리 좌표로 확인한다.
    public bool IsPlacementDrifted()
    {
        if (!IsVisible) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!DisplayInfo.GetWindowRect(hwnd, out var cur)) return false;

        var target = PickMonitor();
        if (target.Work.Right == 0 && target.Work.Bottom == 0) return false;

        return cur.Right != target.Work.Right
               || cur.Top < target.Work.Top
               || cur.Bottom > target.Work.Bottom
               || Math.Abs(Width - BaseW) > 0.5
               || Math.Abs(Height - BaseH) > 0.5;
    }

    public void ShowSidebar()
    {
        // Show() 로 hwnd 를 먼저 확보해야 물리 좌표 배치가 가능하다. 투명도 0 이라 깜빡임은 없다.
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            ApplyPlacement("show");
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        else
        {
            ApplyPlacement("show");
        }
        if (Pinned) Expand();
    }

    public void HideSidebar()
    {
        if (!IsVisible) return;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        anim.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void Expand()
    {
        _collapseTimer.Stop();
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void Collapse() => DetailPanel.Visibility = Visibility.Hidden;

    private void OnStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !DisplayInfo.GetWindowRect(hwnd, out var cur)) return;

        // 창 중심이 있는 모니터에 붙인다 — 보조 모니터로 끌어다 놓을 수 있어야 한다.
        var mon = DisplayInfo.ForPoint(cur.Left + cur.Width / 2, cur.Top + cur.Height / 2);
        _savedMonitor = mon.Name;
        _savedPhysY = cur.Top;
        ApplyPlacement("drag");

        if (DisplayInfo.GetWindowRect(hwnd, out var final))
        {
            _savedPhysY = final.Top;
            PlacementChanged?.Invoke(mon.Name, final.Top);
        }
    }

    private Pill MakePill(string letter, string name, Color color)
    {
        var pill = new Pill { BaseColor = color };

        var grid = new Grid { Width = PillW, Height = PillH };
        grid.Clip = new RectangleGeometry(new Rect(0, 0, PillW, PillH), PillW / 2, PillW / 2);
        grid.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0xC8, 0x1F, 0x1E, 0x1D)) });
        pill.Fill = new Rectangle
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = 0,
            Fill = new SolidColorBrush(color)
        };
        grid.Children.Add(pill.Fill);

        var texts = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        pill.LetterText = new TextBlock
        {
            Text = letter,
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        pill.Digits = new TextBlock
        {
            FontSize = 8.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = 9.5,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 1, 0, 0)
        };
        texts.Children.Add(pill.LetterText);
        texts.Children.Add(pill.Digits);
        grid.Children.Add(texts);

        PillStrip.Children.Add(new Border
        {
            Child = grid,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(PillW / 2 + 1),
            Margin = new Thickness(0, 3, 0, 3)
        });

        var head = new DockPanel { Margin = new Thickness(0, 4, 0, 3) };
        pill.RowPct = new TextBlock
        {
            Text = "--",
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xEF, 0xE8))
        };
        DockPanel.SetDock(pill.RowPct, Dock.Right);
        pill.RowName = new TextBlock
        {
            Text = name,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD3, 0xD1, 0xC7))
        };
        head.Children.Add(pill.RowPct);
        head.Children.Add(pill.RowName);
        DetailRows.Children.Add(head);

        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF))
        };
        pill.RowFill = new Rectangle
        {
            Height = 4,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            RadiusX = 2,
            RadiusY = 2,
            Fill = new SolidColorBrush(color)
        };
        track.Child = pill.RowFill;
        DetailRows.Children.Add(track);

        pill.RowReset = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x87, 0x80)),
            Margin = new Thickness(0, 2, 0, 2)
        };
        DetailRows.Children.Add(pill.RowReset);
        return pill;
    }

    private void BuildHeader()
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var ver = new TextBlock
        {
            Text = UpdateChecker.VersionText,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x87, 0x80)),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(ver, Dock.Right);
        head.Children.Add(ver);
        head.Children.Add(new TextBlock
        {
            Text = "Claude 사용량",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xEF, 0xE8))
        });
        DetailRows.Children.Add(head);
        DetailRows.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF))
        });
    }

    private void BuildFooter()
    {
        DetailRows.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 5, 0, 5)
        });

        var dock = new DockPanel();
        _pinBtn = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _pinBtn.MouseLeftButtonUp += (_, _) =>
        {
            Pinned = !Pinned;
            PinnedChanged?.Invoke(Pinned);
        };
        DockPanel.SetDock(_pinBtn, Dock.Right);
        var refresh = new TextBlock
        {
            Text = "새로고침",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB5, 0xD4, 0xF4)),
            Cursor = Cursors.Hand
        };
        refresh.MouseLeftButtonUp += (_, _) => RefreshRequested?.Invoke();
        DockPanel.SetDock(refresh, Dock.Right);
        _footer = new TextBlock
        {
            Text = "--",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x87, 0x80))
        };
        dock.Children.Add(_pinBtn);
        dock.Children.Add(refresh);
        dock.Children.Add(_footer);
        DetailRows.Children.Add(dock);
        UpdatePinVisual();

        DetailRows.Children.Add(new TextBlock
        {
            Text = UpdateChecker.BuiltAtText(),
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x6A, 0x64)),
            Margin = new Thickness(0, 2, 0, 0)
        });

        _statusLine = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Foreground = new SolidColorBrush(Red),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand
        };
        _statusLine.MouseLeftButtonUp += (_, _) =>
        {
            if (_statusLine.Text.Contains("로그인")) ReloginRequested?.Invoke();
        };
        DetailRows.Children.Add(_statusLine);
    }

    public void ShowRefreshing() => _footer.Text = "갱신 중…";

    public void FlashThrottled()
    {
        _footer.Text = "5초에 한 번만 갱신돼요";
        if (_flashTimer is null)
        {
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            _flashTimer.Tick += (_, _) =>
            {
                _flashTimer!.Stop();
                _footer.Text = _footerBase;
            };
        }
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void UpdatePinVisual()
    {
        _pinBtn.Text = Pinned ? "\uE77A" : "\uE718";
        _pinBtn.Foreground = new SolidColorBrush(Pinned
            ? Color.FromRgb(0xB5, 0xD4, 0xF4)
            : Color.FromRgb(0x88, 0x87, 0x80));
        _pinBtn.ToolTip = Pinned ? "고정 해제" : "펼침 고정";
    }

    public void ApplySnapshot(UsageSnapshot? s, string? status)
    {
        SetPill(_h, s?.FiveHour?.Utilization);
        SetPill(_w, s?.SevenDay?.Utilization);
        SetPill(_f, s?.ModelWeekly?.Utilization);
        SetPill(_c, s?.ExtraUsagePct);

        if (s?.ModelWeeklyLabel is { Length: > 0 } label)
        {
            _f.LetterText.Text = label[..1].ToUpperInvariant();
            _f.RowName.Text = "주간 · " + label;
        }

        _h.RowReset.Text = FormatCountdown(s?.FiveHour?.ResetsAt);
        _w.RowReset.Text = FormatWeekly(s?.SevenDay?.ResetsAt);
        _f.RowReset.Text = s?.ModelWeekly is null ? "정보 없음" : FormatWeekly(s.ModelWeekly.ResetsAt);
        _c.RowReset.Text = s?.ExtraUsageDetail ??
            (s?.ExtraUsagePct is double x
                ? (x <= 0 ? "크레딧 사용 없음" : "이번 결제 주기 사용률")
                : "정보 없음");

        string src = s?.Source == "FILE" ? "앱 기록" : "API";
        _footerBase = s is null ? "데이터 없음" : $"{s.FetchedAt:HH:mm:ss} 갱신 · {src}";
        _footer.Text = _footerBase;
        _statusLine.Text = status ?? "";
        bool relogin = status?.Contains("로그인") == true;
        _statusLine.TextDecorations = relogin ? TextDecorations.Underline : null;
        _statusLine.ToolTip = relogin ? "클릭하면 로그인 터미널이 열립니다" : null;
        _statusLine.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetPill(Pill p, double? pct)
    {
        if (pct is null)
        {
            p.Digits.Text = "-";
            p.Fill.Height = 0;
            p.RowPct.Text = "--";
            p.RowFill.Width = 0;
            StopPulse(p);
            return;
        }

        int v = (int)Math.Round(pct.Value);
        double clamped = Math.Max(0, Math.Min(100, pct.Value));
        p.Digits.Text = string.Join("\n", v.ToString().ToCharArray());
        p.Fill.Height = PillH * clamped / 100.0;
        p.RowPct.Text = v + "%";
        p.RowFill.Width = RowBarW * clamped / 100.0;

        bool danger = v > 85;
        var color = danger ? Red : p.BaseColor;
        p.Fill.Fill = new SolidColorBrush(color);
        p.RowFill.Fill = new SolidColorBrush(color);
        p.RowPct.Foreground = new SolidColorBrush(danger ? Red : Color.FromRgb(0xF1, 0xEF, 0xE8));
        if (danger) StartPulse(p); else StopPulse(p);
    }

    private static void StartPulse(Pill p)
    {
        if (p.Pulsing) return;
        p.Pulsing = true;
        p.Fill.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1.0, 0.5, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private static void StopPulse(Pill p)
    {
        if (!p.Pulsing) return;
        p.Pulsing = false;
        p.Fill.BeginAnimation(UIElement.OpacityProperty, null);
        p.Fill.Opacity = 1;
    }

    private static string FormatCountdown(DateTimeOffset? t)
    {
        if (t is null) return "리셋 정보 없음";
        var d = t.Value - DateTimeOffset.Now;
        if (d.TotalSeconds <= 0) return "리셋 직후";
        if (d.TotalHours >= 1) return $"리셋까지 {(int)d.TotalHours}시간 {d.Minutes}분";
        return $"리셋까지 {d.Minutes}분";
    }

    private static string FormatWeekly(DateTimeOffset? t)
    {
        if (t is null) return "리셋 정보 없음";
        var local = t.Value.ToLocalTime();
        return $"{local.ToString("ddd", Korean)} {local:HH:mm} 리셋";
    }

    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

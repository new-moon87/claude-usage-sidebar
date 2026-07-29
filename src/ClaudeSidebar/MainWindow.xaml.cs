using System.Globalization;
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
    private double? _savedTop;
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
    public event Action<double>? TopChanged;

    public MainWindow()
    {
        InitializeComponent();
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
        Loaded += (_, _) => Anchor();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 도구 창 + 비활성화 창으로 만들어 Alt-Tab과 포커스 훔침을 방지한다.
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, ex | WsExToolwindow | WsExNoactivate);
    }

    public void SetSavedTop(double top) => _savedTop = top;

    private void Anchor()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width;
        double top = _savedTop ?? (wa.Top + (wa.Height - Height) / 2);
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - Height));
    }

    public void ShowSidebar()
    {
        Anchor();
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
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
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width;
        Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
        _savedTop = Top;
        TopChanged?.Invoke(Top);
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

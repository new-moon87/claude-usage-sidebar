using System.Drawing;
using System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace ClaudeSidebar;

// 트레이 컨텍스트 메뉴를 Windows 11 모양으로 그린다.
// 수치는 index 메모(Theme/Menu.xaml)와 같은 값을 쓴다 — 두 앱이 한 사람 손에서 나온 것으로 보여야 한다.
//
// WinForms 기본 메뉴는 각지고 회색 이미지 여백이 붙어 혼자 튄다.
// 바깥 8px, 항목 강조 4px 곡률, 구분선은 좌우 12px 들여쓴다.
internal sealed class MenuTheme : WF.ToolStripRenderer
{
    private static readonly Color Surface = Color.FromArgb(0xFB, 0xFB, 0xFD);
    private static readonly Color BorderLine = Color.FromArgb(0x1F, 0, 0, 0);
    private static readonly Color TextColor = Color.FromArgb(0x1F, 0x1F, 0x23);
    private static readonly Color TextDisabled = Color.FromArgb(0x9A, 0x9A, 0xA2);
    private static readonly Color Hover = Color.FromArgb(0x14, 0x8C, 0x52, 0xFF);
    private static readonly Color Pressed = Color.FromArgb(0x26, 0x8C, 0x52, 0xFF);
    private static readonly Color SeparatorLine = Color.FromArgb(0x1A, 0, 0, 0);
    private static readonly Color Accent = Color.FromArgb(0x8C, 0x52, 0xFF);

    // index 의 수치는 WPF DIP 다. WinForms 렌더러는 물리 픽셀로 그리므로 모니터 배율만큼 키워야 한다.
    // 이걸 빼먹으면 200% 모니터에서 곡률·여백·구분선 들여쓰기가 전부 절반으로 보인다.
    private const int OuterRadiusDip = 8;
    private const int ItemRadiusDip = 4;
    private const int ItemInsetXDip = 4;   // 항목 강조의 좌우 여백 (index: Margin="4,1")
    private const int ItemInsetYDip = 1;
    private const int SeparatorInsetDip = 12;

    // 체크는 왼쪽에 여백을 두고 조금 작게 그린다 — 칸 안에 체크가 들어앉은 느낌을 준다.
    private const int CheckLeftPadDip = 7;
    private const int CheckWidthDip = 10;
    private const int CheckHeightDip = 8;

    // 버전·카피라이트 같은 설명 줄. 체크 칸만큼 들여쓰지 않고 왼쪽에 붙여 폭 낭비를 줄인다.
    public const string CaptionTag = "caption";
    private const int CaptionLeftDip = 10;

    private static int S(WF.ToolStrip? ts, double dip) => (int)Math.Round(dip * (ts?.DeviceDpi ?? 96) / 96.0);

    /// 설명 줄(버전·날짜·카피라이트) 전용 항목.
    /// 체크 칸을 쓰지 않으므로 그만큼 폭도 요구하지 않는다 — 기본 항목을 쓰면
    /// 가장 긴 이 줄이 쓰지도 않는 여백까지 끌고 와 메뉴가 불필요하게 넓어진다.
    internal sealed class CaptionItem : WF.ToolStripMenuItem
    {
        public CaptionItem(string text) : base(text)
        {
            Enabled = false;
            Tag = CaptionTag;
        }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            var b = base.GetPreferredSize(constrainingSize);
            var text = WF.TextRenderer.MeasureText(Text, Font);
            int dpi = Owner?.DeviceDpi ?? 96;
            int pad = (int)Math.Round((CaptionLeftDip + 14) * dpi / 96.0);
            return new Size(Math.Min(b.Width, text.Width + pad), b.Height);
        }
    }

    public static void Apply(WF.ContextMenuStrip menu)
    {
        menu.Renderer = new MenuTheme();
        menu.BackColor = Surface;
        menu.ForeColor = TextColor;
        menu.Font = new Font("Segoe UI", 9F);
        menu.Padding = new WF.Padding(0, 4, 0, 4);
        menu.ShowImageMargin = true;    // 체크 표시가 들어갈 왼쪽 칸을 남긴다
        menu.DropShadowEnabled = true;

        // 둥근 모서리는 창 영역을 잘라서 만든다. 크기는 열릴 때 정해지므로 그때마다 다시 잡는다.
        void Reshape(object? _, EventArgs __)
        {
            if (menu.Width <= 0 || menu.Height <= 0) return;
            using var path = Rounded(new Rectangle(0, 0, menu.Width, menu.Height), S(menu, OuterRadiusDip));
            menu.Region = new Region(path);
        }
        menu.Opened += Reshape;
        menu.SizeChanged += Reshape;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0 || r.Width <= d || r.Height <= d)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnRenderToolStripBackground(WF.ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Surface);
        using var path = Rounded(new Rectangle(0, 0, e.ToolStrip.Width, e.ToolStrip.Height), S(e.ToolStrip, OuterRadiusDip));
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(WF.ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(BorderLine, Math.Max(1, S(e.ToolStrip, 1)));
        using var path = Rounded(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), S(e.ToolStrip, OuterRadiusDip));
        e.Graphics.DrawPath(pen, path);
    }

    // 왼쪽 체크 칸에 회색 띠를 두지 않는다. index 는 그 자리가 그냥 배경이다.
    protected override void OnRenderImageMargin(WF.ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(WF.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Enabled || !e.Item.Selected) return;

        int insetX = S(e.ToolStrip, ItemInsetXDip), insetY = S(e.ToolStrip, ItemInsetYDip);
        var r = new Rectangle(insetX, insetY, e.Item.Width - insetX * 2, e.Item.Height - insetY * 2);
        if (r.Width <= 0 || r.Height <= 0) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(e.Item.Pressed ? Pressed : Hover);
        using var path = Rounded(r, S(e.ToolStrip, ItemRadiusDip));
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(WF.ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        int inset = S(e.ToolStrip, SeparatorInsetDip);
        using var pen = new Pen(SeparatorLine, Math.Max(1, S(e.ToolStrip, 1)));
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
    }

    protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? TextColor : TextDisabled;

        // 설명 줄은 체크 칸을 비워 두지 않고 왼쪽 끝에서 시작한다.
        if (e.Item.Tag as string == CaptionTag)
        {
            int left = S(e.ToolStrip, CaptionLeftDip);
            var r = e.TextRectangle;
            e.TextRectangle = new Rectangle(left, r.Y, Math.Max(1, r.Right - left), r.Height);
            e.TextFormat = (e.TextFormat & ~WF.TextFormatFlags.HorizontalCenter) | WF.TextFormatFlags.Left;
        }

        base.OnRenderItemText(e);
    }

    // 체크 표시. 기본 사각 테두리 대신 index 와 같은 보라색 체크 글리프를 그린다.
    protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
    {
        var b = e.ImageRectangle;
        float w = S(e.ToolStrip, CheckWidthDip);
        float h = S(e.ToolStrip, CheckHeightDip);
        float ox = b.Left + S(e.ToolStrip, CheckLeftPadDip);
        float oy = b.Top + (b.Height - h) / 2f;
        float sx = w / 11f, sy = h / 9f;

        PointF P(float x, float y) => new(ox + x * sx, oy + y * sy);
        var pts = new[]
        {
            P(0, 4.5f), P(3.6f, 8f), P(11f, 0.6f), P(9.7f, 0f), P(3.6f, 6f), P(1f, 3.6f)
        };

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(e.Item.Enabled ? Accent : TextDisabled);
        e.Graphics.FillPolygon(brush, pts);
    }
}

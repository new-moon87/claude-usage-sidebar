using System.Drawing;
using WF = System.Windows.Forms;

namespace ClaudeSidebar;

public class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;

    public event Action? RefreshRequested;
    public event Action? ReloginRequested;
    public event Action<bool>? ForceShowChanged;
    public event Action<bool>? AutostartChanged;
    public event Action? ExitRequested;

    public TrayIcon(bool forceShow, bool autostart)
    {
        var menu = new WF.ContextMenuStrip();

        var refreshItem = new WF.ToolStripMenuItem("지금 새로고침");
        refreshItem.Click += (_, _) => RefreshRequested?.Invoke();

        var reloginItem = new WF.ToolStripMenuItem("Claude 재로그인 (터미널 열기)");
        reloginItem.Click += (_, _) => ReloginRequested?.Invoke();

        var forceShowItem = new WF.ToolStripMenuItem("항상 표시") { Checked = forceShow, CheckOnClick = true };
        forceShowItem.CheckedChanged += (_, _) => ForceShowChanged?.Invoke(forceShowItem.Checked);

        var autostartItem = new WF.ToolStripMenuItem("Windows 시작 시 실행") { Checked = autostart, CheckOnClick = true };
        autostartItem.CheckedChanged += (_, _) => AutostartChanged?.Invoke(autostartItem.Checked);

        var exitItem = new WF.ToolStripMenuItem("종료");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(refreshItem);
        menu.Items.Add(reloginItem);
        menu.Items.Add(forceShowItem);
        menu.Items.Add(autostartItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new WF.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Claude 사용량 사이드바",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => RefreshRequested?.Invoke();
    }

    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var purple = new SolidBrush(Color.FromArgb(127, 119, 221));
            using var amber = new SolidBrush(Color.FromArgb(239, 159, 39));
            using var blue = new SolidBrush(Color.FromArgb(55, 138, 221));
            g.FillRectangle(purple, 2, 3, 3, 10);
            g.FillRectangle(amber, 6, 3, 3, 10);
            g.FillRectangle(blue, 10, 3, 3, 10);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

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

        // 버전과 이 PC 에 올라온 날짜. 누를 일이 없으므로 비활성 항목으로 둔다.
        var about = new WF.ToolStripMenuItem($"Claude 사용량 {UpdateChecker.VersionText}") { Enabled = false };
        var builtAt = UpdateChecker.BuiltAtText();
        var builtAtItem = builtAt.Length > 0
            ? new WF.ToolStripMenuItem(builtAt) { Enabled = false }
            : null;

        // 카피라이트. 트레이 메뉴는 SVG 를 못 쓰는 매체라 봉투 아이콘 대신 원문자 U+24D4 를 쓴다.
        // 인코딩 계층을 타다 글자가 깨지는 자리라 이스케이프로 박는다.
        var copyrightItem = new WF.ToolStripMenuItem(
            "\u00A9 2026 Lee-wonrae. All rights reserved.  \u24D4 new_moon@kakao.com") { Enabled = false };

        var siteItem = new WF.ToolStripMenuItem("다운로드 사이트 열기");
        siteItem.Click += (_, _) => OpenSite();

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

        menu.Items.Add(about);
        if (builtAtItem is not null) menu.Items.Add(builtAtItem);
        menu.Items.Add(copyrightItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(refreshItem);
        menu.Items.Add(reloginItem);
        menu.Items.Add(siteItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(forceShowItem);
        menu.Items.Add(autostartItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new WF.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = $"Claude 사용량 사이드바 {UpdateChecker.VersionText}",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => RefreshRequested?.Invoke();

        // 메뉴가 실제로 어떻게 구성됐는지 남긴다 — 트레이 메뉴는 눈으로만 확인되는 곳이라
        // 항목이 빠져도 조용히 지나간다.
        Log.Write("[tray] 메뉴: " + string.Join(" / ",
            menu.Items.OfType<WF.ToolStripItem>().Select(i => i is WF.ToolStripSeparator ? "—" : i.Text)));
    }

    // 배포 사이트를 기본 브라우저로 연다. 실패해도 트레이가 죽으면 안 된다.
    private static void OpenSite()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateChecker.SiteUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write("[tray] 사이트 열기 실패: " + ex.Message);
        }
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

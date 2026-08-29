using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace ClaudeSidebar;

/// 컨텍스트 메뉴 모양을 확인하기 위한 진단 모드.
///   ClaudeSidebar.exe --menu-preview <결과.png>
///
/// 실제 메뉴 객체를 그대로 띄워 찍으므로 사용자가 보는 것과 같은 결과가 나온다.
/// 마우스를 건드리지 않고 검증할 수 있다.
internal static class MenuPreview
{
    public static void Render(string outputPath, Action done)
    {
        // 배경이 바탕화면이면 둥근 모서리 밖으로 남의 화면이 찍힌다. 중립 판을 깔고 그 위에 띄운다.
        var backdrop = new WF.Form
        {
            FormBorderStyle = WF.FormBorderStyle.None,
            StartPosition = WF.FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = Color.FromArgb(0xED, 0xED, 0xE8),
            Bounds = new Rectangle(20, 20, 1400, 900)
        };
        backdrop.Show();
        backdrop.Activate();

        // 체크 표시까지 확인해야 하므로 켜진 상태로 만든다.
        var tray = new TrayIcon(true, true, visible: false);
        var menu = tray.Menu;
        // 소유 창 없이 띄우면 활성화를 잃는 순간 스스로 닫힌다. 배경판을 소유자로 준다.
        menu.Show(backdrop, new Point(40, 40));

        // 다른 창이 포커스를 가져가면 메뉴가 스스로 닫힌다. 몇 번 다시 띄워 본다.
        int attempts = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            if (!menu.Visible && ++attempts <= 4)
            {
                Log.Write($"[preview] 메뉴가 닫혀 있어 다시 띄운다 ({attempts})");
                backdrop.Activate();
                menu.Show(backdrop, new Point(40, 40));
                return;
            }

            timer.Stop();
            try
            {
                if (!menu.Visible) throw new InvalidOperationException("menu not visible");
                var r = Rectangle.Inflate(menu.Bounds, 16, 16);
                using var bmp = new Bitmap(r.Width, r.Height);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                bmp.Save(outputPath, ImageFormat.Png);
                Log.Write($"[preview] 저장: {outputPath} ({r.Width}x{r.Height})");
            }
            catch (Exception ex)
            {
                Log.Write("[preview] 실패: " + ex.Message);
            }
            menu.Close();
            tray.Dispose();
            backdrop.Close();
            done();
        };
        timer.Start();
    }
}

using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeSidebar;

// 모니터 정보는 절대 캐시하지 않는다. 호출할 때마다 OS에 새로 묻는다.
// 좌표는 전부 물리 픽셀(Win32 기준)이다. WPF의 DIP와 섞지 말 것.
public static class DisplayInfo
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public record Mon(string Name, RECT Monitor, RECT Work, uint DpiX, uint DpiY, bool Primary)
    {
        public double Scale => DpiX / 96.0;
        public override string ToString() =>
            $"{Name}{(Primary ? "*" : "")} rect={Monitor} work={Work} dpi={DpiX} ({Scale:0.##}x)";
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorInfoPrimary = 1;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpMoveOnly = SwpNoSize | SwpNoZOrder | SwpNoActivate;

    private static Mon Read(IntPtr h)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(h, ref mi);
        uint dx = 96, dy = 96;
        try { GetDpiForMonitor(h, 0, out dx, out dy); } catch { }
        if (dx == 0) dx = 96;
        if (dy == 0) dy = 96;
        return new Mon(mi.szDevice, mi.rcMonitor, mi.rcWork, dx, dy, (mi.dwFlags & MonitorInfoPrimary) != 0);
    }

    public static List<Mon> Enumerate()
    {
        var list = new List<Mon>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            try { list.Add(Read(h)); } catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static Mon ForWindow(IntPtr hwnd) => Read(MonitorFromWindow(hwnd, MonitorDefaultToNearest));

    public static Mon ForPoint(int x, int y) => Read(MonitorFromPoint(new POINT { X = x, Y = y }, MonitorDefaultToNearest));

    // 모니터 구성 전체를 한 줄씩 남긴다. 구성이 바뀌었는지 비교할 지문도 돌려준다.
    public static string LogAll(string tag)
    {
        var mons = Enumerate();
        var sb = new StringBuilder();
        foreach (var m in mons)
        {
            Log.Write($"[display:{tag}] {m}");
            sb.Append(m.Name).Append(m.Monitor).Append(m.Work).Append(m.DpiX).Append('|');
        }
        if (mons.Count == 0) Log.Write($"[display:{tag}] 모니터 열거 결과 없음");
        return sb.ToString();
    }

    public static string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var m in Enumerate())
            sb.Append(m.Name).Append(m.Monitor).Append(m.Work).Append(m.DpiX).Append('|');
        return sb.ToString();
    }
}

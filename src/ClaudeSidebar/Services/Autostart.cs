using Microsoft.Win32;

namespace ClaudeSidebar;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ClaudeSidebar";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled && Environment.ProcessPath is string exe)
                key.SetValue(Name, "\"" + exe + "\"");
            else if (!enabled)
                key.DeleteValue(Name, false);
        }
        catch (Exception ex)
        {
            Log.Write("autostart error: " + ex.Message);
        }
    }
}

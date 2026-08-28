using System.IO;
using System.Text.Json;

namespace ClaudeSidebar;

public class SettingsStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSidebar");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            Log.Write($"[settings] 로드: monitor={Settings.MonitorName ?? "(없음)"} physY={Settings.PhysY?.ToString() ?? "(없음)"} " +
                      $"top={Settings.Top?.ToString("0.#") ?? "(없음)"} pinned={Settings.Pinned} forceShow={Settings.ForceShow}");
        }
        catch (Exception ex)
        {
            // 조용히 삼키면 "창이 엉뚱한 자리에 뜬다"의 원인을 추적할 수 없다.
            Log.Write("[settings] 로드 실패 — 기본값으로 되돌림: " + ex.Message);
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

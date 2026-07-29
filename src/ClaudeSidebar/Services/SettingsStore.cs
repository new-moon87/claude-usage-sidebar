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
        }
        catch
        {
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

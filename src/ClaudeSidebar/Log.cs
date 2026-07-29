using System.IO;

namespace ClaudeSidebar;

public static class Log
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeSidebar");
    private static readonly string FilePath = Path.Combine(Dir, "log.txt");
    private static readonly object Gate = new();

    // 토큰 등 비밀 값은 절대 이 로그로 보내지 않는다.
    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512_000)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, $"[{DateTime.Now:MM-dd HH:mm:ss}] {msg}\r\n");
            }
        }
        catch { }
    }
}

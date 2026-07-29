using System.IO;
using System.Text.Json;

namespace ClaudeSidebar;

// Claude Code 데스크톱 앱이 5분마다 기록하는 plan-usage-history.json의 마지막 샘플을 읽는다.
// API를 못 쓰는 상황의 폴백 + 앱 시작 직후의 즉시 표시용.
public class UsageHistoryReader
{
    public static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json");

    public UsageSnapshot? ReadLast()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return null;
            using var fs = new FileStream(HistoryPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("samples", out var samples) ||
                samples.ValueKind != JsonValueKind.Array || samples.GetArrayLength() == 0)
                return null;

            var last = samples[samples.GetArrayLength() - 1];
            if (!last.TryGetProperty("u", out var u)) return null;

            var snap = new UsageSnapshot { Source = "FILE" };
            if (u.TryGetProperty("fh", out var fh) && fh.ValueKind == JsonValueKind.Number)
                snap.FiveHour = new UsageBucket(fh.GetDouble(), null);
            if (u.TryGetProperty("sd", out var sd) && sd.ValueKind == JsonValueKind.Number)
                snap.SevenDay = new UsageBucket(sd.GetDouble(), null);
            if (u.TryGetProperty("xu", out var xu) && xu.ValueKind == JsonValueKind.Number)
                snap.ExtraUsagePct = xu.GetDouble();
            if (last.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Number)
                snap.FetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(t.GetInt64()).ToLocalTime();
            return snap;
        }
        catch (Exception ex)
        {
            Log.Write("history read error: " + ex.Message);
            return null;
        }
    }
}

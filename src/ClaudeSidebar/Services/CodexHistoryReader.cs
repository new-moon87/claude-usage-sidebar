using System.IO;
using System.Text.Json;

namespace ClaudeSidebar;

// Codex CLI가 세션마다 남기는 rollout 기록에서 마지막 한도 스냅샷을 읽는다.
// ~/.codex/sessions/<년>/<월>/<일>/rollout-*.jsonl 의 payload.type == "token_count" 줄:
//   rate_limits { limit_id, primary{used_percent,window_minutes,resets_at}, secondary{...}, credits{...}, plan_type }
//
// 주의: 이 기록은 CLI 전용이다. Codex 데스크톱 앱은 스레드를 SQLite에 넣고 한도는 아예 남기지 않는다
// (thread_history_1.sqlite 에 rate_limits 0건 — 실측). CLI 를 쓰는 동안에는 실시간으로 쌓이지만
// 한동안 안 쓰면 그대로 낡으므로, 화면에 소스와 경과일을 같이 띄운다.
public class CodexHistoryReader
{
    private static readonly string SessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
    private const int ShortWindowMaxMinutes = 24 * 60;
    // 세션 폴더는 통째로 수백 MB까지 자란다(실측 493MB). 파일 하나가 100MB를 넘을 수 있으므로
    // 절대 통째로 읽지 않는다. 마지막 한도 줄은 파일 끝 근처에 있으니 꼬리만 본다.
    private const int TailBytes = 256 * 1024;

    private string _cachedPath = "";
    private DateTime _cachedStamp;
    private CodexSnapshot? _cached;

    public CodexSnapshot? ReadLast()
    {
        try
        {
            if (!Directory.Exists(SessionsDir)) return null;

            FileInfo? newest = null;
            foreach (var path in Directory.EnumerateFiles(SessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(path);
                if (newest is null || fi.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = fi;
            }
            if (newest is null) return null;

            // 파일이 그대로면 다시 읽지 않는다 — 이 함수는 60초마다 불린다.
            if (newest.FullName == _cachedPath && newest.LastWriteTimeUtc == _cachedStamp) return _cached;

            // 계정 전체 한도가 담긴 줄을 우선한다. 모델별 한도는 안 쓴 모델이면 0% 라
            // 그대로 보여 주면 계정 사용량이 0 인 것처럼 보인다(함정 ⑲).
            CodexSnapshot? firstAny = null;
            foreach (var line in TailLines(newest))
            {
                if (!line.Contains("\"rate_limits\"")) continue;
                var snap = ParseLine(line);
                if (snap is null) continue;
                firstAny ??= snap;
                if (!IsAccountWide(snap)) continue;
                return Cache(newest, snap);
            }
            return firstAny is null ? null : Cache(newest, firstAny);
        }
        catch (Exception ex)
        {
            Log.Write("[codex] history read error: " + ex.Message);
            return null;
        }
    }

    // 계정 전체 한도인지. 기록 파일의 limit_id 는 계정 전체면 codex/premium, 모델별이면 그 모델 id 다.
    private static bool IsAccountWide(CodexSnapshot snap)
    {
        var label = snap.Short?.Label ?? snap.Long?.Label ?? "";
        return label.Length == 0 || label == "codex" || label == "premium";
    }

    private CodexSnapshot Cache(FileInfo file, CodexSnapshot snap)
    {
        snap.FetchedAt = new DateTimeOffset(file.LastWriteTimeUtc).ToLocalTime();
        _cachedPath = file.FullName;
        _cachedStamp = file.LastWriteTimeUtc;
        _cached = snap;
        return snap;
    }

    // 파일 끝 TailBytes 만 읽어 뒤에서 앞으로 한 줄씩 돌려준다.
    // 잘린 첫 줄은 JSON 이 깨져 있으므로 버린다(꼬리를 자른 지점이 줄 중간일 수 있다).
    private static IEnumerable<string> TailLines(FileInfo file)
    {
        using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - TailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        var lines = sr.ReadToEnd().Split('\n');

        int first = start > 0 ? 1 : 0;
        for (int i = lines.Length - 1; i >= first; i--)
        {
            var line = lines[i].Trim();
            if (line.Length > 0) yield return line;
        }
    }

    private static CodexSnapshot? ParseLine(string line)
    {
        // 꼬리만 읽으므로 깨진 줄이 섞일 수 있다. 여기서 터지면 나머지 줄을 못 보므로 조용히 넘긴다.
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
        using (doc)
        {
            return ParseRateLimits(doc.RootElement);
        }
    }

    private static CodexSnapshot? ParseRateLimits(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object) return null;

        var snap = new CodexSnapshot { Source = "FILE" };
        if (rl.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
            snap.PlanType = pt.GetString();
        string label = rl.TryGetProperty("limit_id", out var li) && li.ValueKind == JsonValueKind.String
            ? li.GetString() ?? "" : "";

        var windows = new List<CodexWindow>();
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) continue;
            if (!w.TryGetProperty("window_minutes", out var wm) || wm.ValueKind != JsonValueKind.Number) continue;

            DateTimeOffset? resets = w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).ToLocalTime() : null;
            windows.Add(new CodexWindow(up.GetDouble(), resets, label, wm.GetInt32() * 60));
        }
        if (windows.Count == 0) return null;

        foreach (var w in windows)
        {
            bool isShort = w.WindowSeconds <= ShortWindowMaxMinutes * 60;
            if (isShort && (snap.Short is null || w.Percent > snap.Short.Percent)) snap.Short = w;
            if (!isShort && (snap.Long is null || w.Percent > snap.Long.Percent)) snap.Long = w;
        }

        if (rl.TryGetProperty("credits", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            if (c.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True)
                snap.CreditDetail = "크레딧 무제한";
            else if (c.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True)
                snap.CreditDetail = "크레딧 있음";
            else
                snap.CreditDetail = "크레딧 없음";
        }
        return snap;
    }
}

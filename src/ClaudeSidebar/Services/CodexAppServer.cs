using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudeSidebar;

// Codex 자기 바이너리를 stdio JSON-RPC 서버로 잠깐 띄워 계정 한도를 물어본다.
//
// 왜 HTTP 를 안 쓰나: chatgpt.com 사용량 엔드포인트는 앞단 봇 완화가 Windows 의 TLS 스택을 막는다.
// .NET·curl 은 조용한 상태의 첫 요청도 403 HTML 을 받고, 같은 순간 파이썬은 200 을 받는다(실측).
// app-server 는 그 요청을 Codex 가 자기 스택으로 대신 보내 주므로 통과하고, 덤으로
// **이 앱이 토큰을 읽거나 갱신하거나 되쓸 일이 없어진다.**
//
// 왜 rollout 기록만으로는 안 되나: 기록에는 그 요청에 적용된 한도 하나만 실린다.
// 특정 모델만 쓰는 세션이면 그 모델 한도(보통 0%)만 남고 계정 전체 한도는 아예 안 나온다 — 실측.
public static class CodexAppServer
{
    // 응답은 여기만 camelCase 다(HTTP 응답과 rollout 기록은 snake_case, 서로 필드명도 다르다).
    //   result.rateLimits            계정 전체(limitId "codex")
    //   result.rateLimitsByLimitId   모델별 포함 전부
    //   창: { usedPercent, windowDurationMins, resetsAt }
    private const int ShortWindowMaxMinutes = 24 * 60;

    public static async Task<CodexSnapshot?> ReadAsync(TimeSpan timeout)
    {
        var exe = FindExe();
        if (exe is null)
        {
            Log.Write("[codex] app-server 실행 파일을 못 찾음 — 기록 파일만 쓴다");
            return null;
        }

        using var cts = new CancellationTokenSource(timeout);
        Process? proc = null;
        try
        {
            proc = Process.Start(new ProcessStartInfo(exe, "app-server")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            });
            if (proc is null) return null;

            var stdin = proc.StandardInput;
            var stdout = proc.StandardOutput;

            await Send(stdin, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"clientInfo":{"name":"codex","title":"ClaudeSidebar","version":"1.0.0"}}}""");
            if (await ReadResult(stdout, 1, cts.Token) is null)
            {
                Log.Write("[codex] app-server initialize 응답 없음");
                return null;
            }
            await Send(stdin, """{"jsonrpc":"2.0","method":"initialized","params":{}}""");
            await Send(stdin, """{"jsonrpc":"2.0","id":2,"method":"account/rateLimits/read","params":{}}""");

            var result = await ReadResult(stdout, 2, cts.Token);
            if (result is null)
            {
                Log.Write("[codex] app-server rateLimits 응답 없음");
                return null;
            }
            using var doc = JsonDocument.Parse(result);
            return Parse(doc.RootElement);
        }
        catch (Exception ex)
        {
            Log.Write("[codex] app-server 오류: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    private static Task Send(StreamWriter stdin, string json)
    {
        stdin.Write(json);
        stdin.Write('\n');
        return stdin.FlushAsync();
    }

    // 원하는 id 의 result 만 골라 돌려준다. 그 사이에 오는 알림 줄은 그냥 흘린다.
    private static async Task<string?> ReadResult(StreamReader stdout, int id, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await stdout.ReadLineAsync(ct);
            if (line is null) return null;
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                if (idEl.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var err))
                {
                    Log.Write("[codex] app-server 오류 응답: " + err.ToString()[..Math.Min(160, err.ToString().Length)]);
                    return null;
                }
                return root.TryGetProperty("result", out var res) ? res.GetRawText() : null;
            }
        }
        return null;
    }

    // 알약에는 **계정 전체 한도만** 쓴다(`rateLimits`, limitId "codex").
    // 모델별 한도(`rateLimitsByLimitId` 의 나머지)를 섞으면, 안 쓴 모델의 0% 가 계정 사용량인 양 보인다 —
    // 실제로 계정 전체가 26% 인데 GH 가 0% 로 뜨던 원인이다.
    // 계정에 그 길이의 창이 아예 없을 수도 있다(prolite 는 주간만 있고 5시간 창이 없다). 그때는 null 로 둔다.
    private static CodexSnapshot Parse(JsonElement result)
    {
        var snap = new CodexSnapshot { FetchedAt = DateTimeOffset.Now, Source = "CLI" };
        if (!result.TryGetProperty("rateLimits", out var account) || account.ValueKind != JsonValueKind.Object)
            return snap;

        if (account.TryGetProperty("planType", out var pt) && pt.ValueKind == JsonValueKind.String)
            snap.PlanType = pt.GetString();
        snap.CreditDetail = ReadCredits(account);

        var windows = new List<CodexWindow>();
        Collect(account, windows);
        foreach (var w in windows)
        {
            bool isShort = w.WindowSeconds <= ShortWindowMaxMinutes * 60;
            if (isShort && (snap.Short is null || w.Percent > snap.Short.Percent)) snap.Short = w;
            if (!isShort && (snap.Long is null || w.Percent > snap.Long.Percent)) snap.Long = w;
        }
        return snap;
    }

    private static void Collect(JsonElement limit, List<CodexWindow> into)
    {
        if (limit.ValueKind != JsonValueKind.Object) return;
        // 사람이 읽을 이름이 있으면 그걸 쓴다. limitId 원문("codex_bengalfox")은 화면에 낼 값이 아니다.
        string label = "";
        if (limit.TryGetProperty("limitName", out var ln) && ln.ValueKind == JsonValueKind.String)
            label = ln.GetString() ?? "";
        if (label.Length == 0 && limit.TryGetProperty("limitId", out var li) && li.ValueKind == JsonValueKind.String)
            label = li.GetString() ?? "";

        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limit.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!w.TryGetProperty("usedPercent", out var up) || up.ValueKind != JsonValueKind.Number) continue;
            if (!w.TryGetProperty("windowDurationMins", out var wm) || wm.ValueKind != JsonValueKind.Number) continue;

            DateTimeOffset? resets = w.TryGetProperty("resetsAt", out var ra) && ra.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).ToLocalTime() : null;
            into.Add(new CodexWindow(up.GetDouble(), resets, label, wm.GetInt32() * 60));
        }
    }

    private static string? ReadCredits(JsonElement limit)
    {
        if (!limit.TryGetProperty("credits", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        if (c.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True) return "크레딧 무제한";
        bool has = c.TryGetProperty("hasCredits", out var hc) && hc.ValueKind == JsonValueKind.True;
        if (!has) return "크레딧 없음";
        string bal = c.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() ?? "" : "";
        return bal.Length > 0 ? $"크레딧 잔액 {bal}" : "크레딧 있음";
    }

    /// Codex 를 쓴 흔적이 있는 PC인지. 없으면 화면에서 Codex 구역을 통째로 뺀다.
    public static bool IsInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".codex");
        return File.Exists(Path.Combine(dir, "auth.json")) || Directory.Exists(Path.Combine(dir, "sessions"));
    }

    // npm 전역 설치본의 네이티브 바이너리를 먼저 찾는다. 런처(codex.cmd)를 거치면 node 가 한 번 더 뜬다.
    private static string? FindExe()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var vendorRoot = Path.Combine(roaming, "npm", "node_modules", "@openai", "codex", "node_modules", "@openai");
        try
        {
            if (Directory.Exists(vendorRoot))
            {
                foreach (var platform in Directory.EnumerateDirectories(vendorRoot, "codex-*"))
                {
                    var hit = Directory.EnumerateFiles(platform, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (hit is not null) return hit;
                }
            }
        }
        catch { }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                foreach (var name in new[] { "codex.exe", "codex.cmd" })
                {
                    var p = Path.Combine(dir.Trim(), name);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
        }
        return null;
    }
}

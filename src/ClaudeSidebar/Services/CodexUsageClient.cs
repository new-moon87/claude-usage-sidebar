using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeSidebar;

// Codex(ChatGPT 백엔드) 사용량. 비공식 엔드포인트라 관대하게 파싱한다.
// 실측 응답(2026-09-15, prolite):
//   plan_type, rate_limit{primary_window,secondary_window},
//   code_review_rate_limit, additional_rate_limits[{limit_name, rate_limit{...}}],
//   credits{has_credits,unlimited,balance}
//   window = { used_percent, limit_window_seconds, reset_after_seconds, reset_at(유닉스 초) }
public class CodexUsageClient
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/codex/usage";
    // User-Agent 가 없으면 앞단이 403 HTML 을 돌려준다(실측). 다른 헤더는 빠져도 200 이 온다 — 함정 ⑰.
    public const string UserAgent = "codex_cli_rs/0.144.1";
    // 24시간을 경계로 단기(5시간대)/장기(주간·월간) 창을 가른다.
    private const int ShortWindowMaxSeconds = 24 * 60 * 60;

    private readonly HttpClient _http;

    public CodexUsageClient(HttpClient http) => _http = http;

    public async Task<(CodexSnapshot? Snapshot, HttpStatusCode Status)> FetchAsync(CodexCredentialStore.Auth auth)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + auth.AccessToken);
        if (auth.AccountId.Length > 0)
            req.Headers.TryAddWithoutValidation("chatgpt-account-id", auth.AccountId);
        req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var why = await resp.Content.ReadAsStringAsync();
            Log.Write($"[codex] usage fetch failed: HTTP {(int)resp.StatusCode} · {Summarize(why)}");
            return (null, resp.StatusCode);
        }
        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            return (Parse(json), resp.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Write("[codex] usage parse error: " + ex.Message);
            return (null, resp.StatusCode);
        }
    }

    // 실패 본문에서 한 줄만 남긴다. 앞단이 막으면 JSON 이 아니라 HTML 이 오므로
    // 그대로 로그에 부으면 로그가 못 쓰게 된다.
    private static string Summarize(string body)
    {
        var flat = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (flat.StartsWith("<")) return "HTML 차단 페이지 " + flat.Length + "자";
        return flat.Length > 160 ? flat[..160] : flat;
    }

    private static CodexSnapshot Parse(string json)
    {
        var snap = new CodexSnapshot { FetchedAt = DateTimeOffset.Now, Source = "API" };
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String)
            snap.PlanType = pt.GetString();

        var windows = new List<CodexWindow>();
        if (root.TryGetProperty("rate_limit", out var rl)) Collect(rl, "전체", windows);
        if (root.TryGetProperty("code_review_rate_limit", out var cr)) Collect(cr, "코드 리뷰", windows);
        if (root.TryGetProperty("additional_rate_limits", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in extra.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string label = item.TryGetProperty("limit_name", out var ln) && ln.ValueKind == JsonValueKind.String
                    ? ln.GetString() ?? "" : "";
                if (item.TryGetProperty("rate_limit", out var sub)) Collect(sub, label, windows);
            }
        }

        // 각 그룹에서 가장 높은 사용률을 고른다 — 먼저 막히는 한도가 사용자에게 의미 있는 값이다.
        snap.Short = Pick(windows, w => w.WindowSeconds <= ShortWindowMaxSeconds);
        snap.Long = Pick(windows, w => w.WindowSeconds > ShortWindowMaxSeconds);
        snap.CreditDetail = ReadCredits(root);
        return snap;
    }

    private static CodexWindow? Pick(List<CodexWindow> all, Func<CodexWindow, bool> match)
    {
        CodexWindow? best = null;
        foreach (var w in all)
            if (match(w) && (best is null || w.Percent > best.Percent)) best = w;
        return best;
    }

    // rate_limit 하나에서 primary_window / secondary_window 를 모두 꺼낸다.
    // 이름이 창 길이를 뜻하지 않으므로 둘을 구분하지 않고 같이 모은다(함정 ⑯).
    private static void Collect(JsonElement rl, string label, List<CodexWindow> into)
    {
        if (rl.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "primary_window", "secondary_window" })
        {
            if (!rl.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!w.TryGetProperty("used_percent", out var up) || up.ValueKind != JsonValueKind.Number) continue;
            if (!w.TryGetProperty("limit_window_seconds", out var ws) || ws.ValueKind != JsonValueKind.Number) continue;

            DateTimeOffset? resets = null;
            if (w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
                resets = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).ToLocalTime();
            else if (w.TryGetProperty("reset_after_seconds", out var ras) && ras.ValueKind == JsonValueKind.Number)
                resets = DateTimeOffset.Now.AddSeconds(ras.GetDouble());

            into.Add(new CodexWindow(up.GetDouble(), resets, label, ws.GetInt32()));
        }
    }

    // balance 는 실측에서 문자열("0")로 온다. 숫자로 올 가능성도 열어 둔다.
    private static string? ReadCredits(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        if (c.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True) return "크레딧 무제한";

        string bal = "";
        if (c.TryGetProperty("balance", out var b))
            bal = b.ValueKind switch
            {
                JsonValueKind.String => b.GetString() ?? "",
                JsonValueKind.Number => b.GetDouble().ToString("0.##"),
                _ => ""
            };
        bool has = c.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True;
        if (!has) return "크레딧 없음";
        return bal.Length > 0 ? $"크레딧 잔액 {bal}" : "크레딧 있음";
    }
}

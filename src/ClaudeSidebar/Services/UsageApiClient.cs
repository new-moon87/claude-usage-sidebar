using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ClaudeSidebar;

// 비공식 엔드포인트이므로 필드 구성이 바뀔 수 있다 → 최대한 관대하게 파싱한다.
// 응답 예시는 docs/usage-sample.json 참고 (2026-07-28 기준).
public class UsageApiClient
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private readonly HttpClient _http;

    public UsageApiClient(HttpClient http) => _http = http;

    public async Task<(UsageSnapshot? Snapshot, HttpStatusCode Status)> FetchAsync(string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Write($"usage fetch failed: HTTP {(int)resp.StatusCode}");
            return (null, resp.StatusCode);
        }
        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            return (Parse(json), resp.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Write("usage parse error: " + ex.Message);
            return (null, resp.StatusCode);
        }
    }

    private static UsageSnapshot Parse(string json)
    {
        var snap = new UsageSnapshot { FetchedAt = DateTimeOffset.Now, Source = "API" };
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        snap.FiveHour = ReadBucket(root, "five_hour");
        snap.SevenDay = ReadBucket(root, "seven_day");

        // 모델 전용 주간 한도: limits[] 중 kind=weekly_scoped + scope.model 항목
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var lim in limits.EnumerateArray())
            {
                if (lim.ValueKind != JsonValueKind.Object) continue;
                if (!lim.TryGetProperty("kind", out var kind) || kind.GetString() != "weekly_scoped") continue;

                double? pct = lim.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDouble() : null;
                DateTimeOffset? resets = lim.TryGetProperty("resets_at", out var r) &&
                    r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var dt)
                    ? dt : null;
                string label = "";
                if (lim.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object &&
                    scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object &&
                    model.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
                    label = dn.GetString() ?? "";

                if (pct is not null)
                {
                    snap.ModelWeekly = new UsageBucket(pct, resets);
                    snap.ModelWeeklyLabel = label;
                    break;
                }
            }
        }
        // 응답 변형 대비: seven_day_<model> 버킷 형태도 봐준다
        if (snap.ModelWeekly is null)
        {
            foreach (var name in new[] { "seven_day_fable", "seven_day_opus", "seven_day_sonnet" })
            {
                var b = ReadBucket(root, name);
                if (b?.Utilization is not null)
                {
                    snap.ModelWeekly = b;
                    snap.ModelWeeklyLabel = name["seven_day_".Length..];
                    break;
                }
            }
        }

        // 추가 사용량 크레딧: spend가 제일 풍부하고, 없으면 extra_usage.utilization
        if (root.TryGetProperty("spend", out var spend) && spend.ValueKind == JsonValueKind.Object)
        {
            if (spend.TryGetProperty("percent", out var sp) && sp.ValueKind == JsonValueKind.Number)
                snap.ExtraUsagePct = sp.GetDouble();
            string used = FormatMoney(spend, "used");
            string limit = FormatMoney(spend, "limit");
            bool enabled = spend.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
            if (used.Length > 0 && limit.Length > 0)
                snap.ExtraUsageDetail = $"{used} / {limit}" + (enabled ? "" : " · 비활성");
        }
        if (snap.ExtraUsagePct is null && root.TryGetProperty("extra_usage", out var xu) &&
            xu.ValueKind == JsonValueKind.Object &&
            xu.TryGetProperty("utilization", out var xup) && xup.ValueKind == JsonValueKind.Number)
        {
            snap.ExtraUsagePct = xup.GetDouble();
        }
        return snap;
    }

    private static UsageBucket? ReadBucket(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return null;
        double? util = el.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() : null;
        DateTimeOffset? resets = el.TryGetProperty("resets_at", out var r) &&
            r.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(r.GetString(), out var dt)
            ? dt : null;
        return util is null && resets is null ? null : new UsageBucket(util, resets);
    }

    private static string FormatMoney(JsonElement spend, string prop)
    {
        if (!spend.TryGetProperty(prop, out var m) || m.ValueKind != JsonValueKind.Object) return "";
        if (!m.TryGetProperty("amount_minor", out var am) || am.ValueKind != JsonValueKind.Number) return "";
        int exp = m.TryGetProperty("exponent", out var ex) && ex.ValueKind == JsonValueKind.Number
            ? ex.GetInt32() : 2;
        double val = am.GetDouble() / Math.Pow(10, exp);
        return "$" + val.ToString(val == Math.Floor(val) ? "0" : "0.00");
    }
}

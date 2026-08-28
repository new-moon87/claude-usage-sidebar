using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeSidebar;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable, Version? LatestVersion, string? DownloadUrl, string? Notes, string? Sha256 = null)
{
    public static UpdateCheckResult None { get; } = new(false, null, null, null);
}

internal sealed class VersionManifest
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    // notes 는 문자열일 수도 배열일 수도 있다. 한쪽만 받게 짜면 배포 형식이 바뀌는 순간
    // 이미 나간 모든 버전이 매니페스트를 못 읽어 업데이트가 통째로 멈춘다(index 메모에서 실제로 겪음).
    [JsonPropertyName("notes")] public JsonElement Notes { get; set; }

    public string? NotesText() => Notes.ValueKind switch
    {
        JsonValueKind.String => Notes.GetString(),
        JsonValueKind.Array => string.Join(Environment.NewLine,
            Notes.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())),
        _ => null,
    };
}

// 새 버전이 있는지 확인만 한다. 내려받기·교체는 UpdateInstaller 몫.
// 실패하면 조용히 넘어간다 — 네트워크가 없다고 사이드바가 오류를 띄우면 안 된다.
// ManifestUrl 을 비우면 이 앱은 업데이트 관련 통신을 전혀 하지 않는다.
internal sealed class UpdateChecker(HttpClient http)
{
    public const string ManifestUrl =
        "https://raw.githubusercontent.com/new-moon87/claude-usage-sidebar/main/update/version.json";

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public string Url { get; init; } = ManifestUrl;

    public async Task<UpdateCheckResult> CheckAsync()
    {
        if (string.IsNullOrWhiteSpace(Url)) return UpdateCheckResult.None;
        try
        {
            var m = await http.GetFromJsonAsync<VersionManifest>(Url);
            if (m?.Version is null || !Version.TryParse(m.Version, out var latest))
            {
                Log.Write("[update] 매니페스트 형식 오류 — 건너뜀");
                return UpdateCheckResult.None;
            }
            bool available = latest > CurrentVersion;
            Log.Write($"[update] 현재 {CurrentVersion} / 최신 {latest} → {(available ? "새 버전 있음" : "최신")}");
            return new UpdateCheckResult(available, latest, m.DownloadUrl, m.NotesText(), m.Sha256);
        }
        catch (Exception ex)
        {
            Log.Write($"[update] 확인 실패(무시): {ex.GetType().Name}");
            return UpdateCheckResult.None;
        }
    }
}

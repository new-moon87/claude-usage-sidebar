using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSidebar;

// Claude Code의 ~/.claude/.credentials.json을 읽고, 만료 시 리프레시한다.
// 토큰 값은 어떤 로그·UI에도 출력하지 않는다.
public class CredentialStore
{
    private static readonly string CredPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";

    private readonly HttpClient _http;
    private DateTimeOffset _refreshBlockedUntil = DateTimeOffset.MinValue;

    public string? LastError { get; private set; }

    public CredentialStore(HttpClient http) => _http = http;

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        try
        {
            if (!File.Exists(CredPath))
            {
                // Claude Code로 로그인한 적 없는 PC → 로그인해야 파일이 생긴다
                LastError = "Claude 로그인 필요";
                return null;
            }
            var root = JsonNode.Parse(File.ReadAllText(CredPath));
            var oauth = root?["claudeAiOauth"];
            if (oauth is null) { LastError = "credentials 파일 형식 오류"; return null; }

            var access = (string?)oauth["accessToken"];
            var refresh = (string?)oauth["refreshToken"];
            var expiresAt = (long?)oauth["expiresAt"] ?? 0;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!forceRefresh && expiresAt - 300_000 > now && !string.IsNullOrEmpty(access))
            {
                LastError = null;
                return access;
            }
            if (string.IsNullOrEmpty(refresh)) { LastError = "재로그인 필요"; return null; }

            if (DateTimeOffset.UtcNow < _refreshBlockedUntil)
            {
                LastError ??= "토큰 갱신 대기 중";
                return string.IsNullOrEmpty(access) ? null : access;
            }

            var body = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = refresh,
                client_id = ClientId
            });
            using var resp = await _http.PostAsync(TokenUrl, new StringContent(body, Encoding.UTF8, "application/json"));
            var text = await resp.Content.ReadAsStringAsync();

            if ((int)resp.StatusCode == 429)
            {
                _refreshBlockedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
                LastError = "토큰 갱신 잠시 대기 (레이트리밋)";
                Log.Write("token refresh rate-limited, backing off 5min");
                return string.IsNullOrEmpty(access) ? null : access;
            }
            if (!resp.IsSuccessStatusCode)
            {
                // 리프레시 토큰이 죽은 상태 → 매 폴링마다 두드리지 않게 10분 백오프
                _refreshBlockedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                LastError = $"재로그인 필요 (HTTP {(int)resp.StatusCode})";
                Log.Write($"token refresh failed: HTTP {(int)resp.StatusCode}");
                return null;
            }

            var tr = JsonNode.Parse(text);
            var newAccess = (string?)tr?["access_token"];
            var newRefresh = (string?)tr?["refresh_token"];
            var expiresIn = (long?)tr?["expires_in"];
            if (string.IsNullOrEmpty(newAccess)) { LastError = "갱신 응답 형식 오류"; return null; }

            WriteBack(refresh!, newAccess!, newRefresh, expiresIn);
            LastError = null;
            return newAccess;
        }
        catch (Exception ex)
        {
            LastError = "credentials 오류";
            Log.Write("credential error: " + ex.Message);
            return null;
        }
    }

    // 갱신된 토큰을 원래 파일 형식 그대로, 원자적으로 되쓴다.
    // 그 사이 다른 프로세스(npm CLI 등)가 먼저 갱신했다면 파일 쪽을 우선한다.
    private void WriteBack(string usedRefresh, string newAccess, string? newRefresh, long? expiresIn)
    {
        try
        {
            var current = JsonNode.Parse(File.ReadAllText(CredPath));
            var oauth = current?["claudeAiOauth"];
            if (oauth is null) return;
            if ((string?)oauth["refreshToken"] != usedRefresh)
            {
                Log.Write("write-back skipped: file changed by another process");
                return;
            }
            oauth["accessToken"] = newAccess;
            if (!string.IsNullOrEmpty(newRefresh)) oauth["refreshToken"] = newRefresh;
            if (expiresIn is not null)
                oauth["expiresAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresIn.Value * 1000;

            File.Copy(CredPath, CredPath + ".bak", true);
            var tmp = CredPath + ".tmp";
            File.WriteAllText(tmp, current!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(tmp, CredPath, true);
            Log.Write("token refreshed and written back");
        }
        catch (Exception ex)
        {
            Log.Write("write-back error: " + ex.Message);
        }
    }
}

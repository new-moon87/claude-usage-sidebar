using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSidebar;

// Codex CLI의 ~/.codex/auth.json을 읽고, 만료 시 리프레시한다.
// 파일 구조(실측): { auth_mode, OPENAI_API_KEY, tokens:{ id_token, access_token, refresh_token, account_id }, last_refresh }
// 토큰 값은 어떤 로그·UI에도 출력하지 않는다. CredentialStore와 같은 불변식을 따른다.
public class CodexCredentialStore
{
    private static readonly string AuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
    // Codex CLI의 공개 OAuth 클라이언트 ID (바이너리에 박혀 배포되는 값이라 비밀이 아니다).
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";

    private readonly HttpClient _http;
    private DateTimeOffset _refreshBlockedUntil = DateTimeOffset.MinValue;

    public string? LastError { get; private set; }

    public CodexCredentialStore(HttpClient http) => _http = http;

    /// Codex 를 쓴 흔적이 있는 PC인지. 없으면 화면에서 Codex 구역을 통째로 뺀다.
    /// 로그인 파일이나 세션 기록 중 하나만 있어도 대상으로 본다(로그아웃 상태여도 안내는 보여야 한다).
    public static bool IsInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return File.Exists(AuthPath) || Directory.Exists(Path.Combine(home, ".codex", "sessions"));
    }

    public record Auth(string AccessToken, string AccountId);

    // 만료 시각은 파일에 없고 JWT 안에만 있다. 그래서 유효기간을 추측하지 않고
    // 일단 그대로 써 본 뒤 401 이 오면 forceRefresh 로 다시 부른다(App 쪽 기존 경로와 같다).
    public async Task<Auth?> GetAuthAsync(bool forceRefresh = false)
    {
        try
        {
            if (!File.Exists(AuthPath))
            {
                LastError = "Codex 로그인 필요";
                return null;
            }
            var root = JsonNode.Parse(File.ReadAllText(AuthPath));
            var tokens = root?["tokens"];
            if (tokens is null) { LastError = "Codex auth 파일 형식 오류"; return null; }

            var access = (string?)tokens["access_token"];
            var refresh = (string?)tokens["refresh_token"];
            var account = (string?)tokens["account_id"] ?? "";

            if (!forceRefresh && !string.IsNullOrEmpty(access))
            {
                LastError = null;
                return new Auth(access!, account);
            }
            if (string.IsNullOrEmpty(refresh)) { LastError = "Codex 재로그인 필요"; return null; }

            if (DateTimeOffset.UtcNow < _refreshBlockedUntil)
            {
                LastError ??= "Codex 토큰 갱신 대기 중";
                return string.IsNullOrEmpty(access) ? null : new Auth(access!, account);
            }

            var body = JsonSerializer.Serialize(new
            {
                client_id = ClientId,
                grant_type = "refresh_token",
                refresh_token = refresh,
                scope = "openid profile email"
            });
            using var tokenReq = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            // 사용량 엔드포인트와 같은 이유로 User-Agent 를 붙인다(없으면 앞단이 막는다 — 함정 ⑰).
            tokenReq.Headers.TryAddWithoutValidation("User-Agent", CodexUsageClient.UserAgent);
            using var resp = await _http.SendAsync(tokenReq);
            var text = await resp.Content.ReadAsStringAsync();

            if ((int)resp.StatusCode == 429)
            {
                _refreshBlockedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
                LastError = "Codex 토큰 갱신 잠시 대기 (레이트리밋)";
                Log.Write("[codex] token refresh rate-limited, backing off 5min");
                return string.IsNullOrEmpty(access) ? null : new Auth(access!, account);
            }
            if (!resp.IsSuccessStatusCode)
            {
                // 리프레시 토큰이 죽은 상태 → 매 폴링마다 두드리지 않게 10분 백오프
                _refreshBlockedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                LastError = $"Codex 재로그인 필요 (HTTP {(int)resp.StatusCode})";
                Log.Write($"[codex] token refresh failed: HTTP {(int)resp.StatusCode}");
                return null;
            }

            var tr = JsonNode.Parse(text);
            var newAccess = (string?)tr?["access_token"];
            if (string.IsNullOrEmpty(newAccess)) { LastError = "Codex 갱신 응답 형식 오류"; return null; }

            WriteBack(refresh!, newAccess!, (string?)tr?["refresh_token"], (string?)tr?["id_token"]);
            LastError = null;
            return new Auth(newAccess!, account);
        }
        catch (Exception ex)
        {
            LastError = "Codex auth 오류";
            Log.Write("[codex] credential error: " + ex.Message);
            return null;
        }
    }

    // 갱신된 토큰을 원래 파일 형식 그대로, 원자적으로 되쓴다.
    // 그 사이 Codex CLI 가 먼저 갱신했다면 파일 쪽을 우선한다(경합에서 남의 토큰을 밟지 않는다).
    private void WriteBack(string usedRefresh, string newAccess, string? newRefresh, string? newId)
    {
        try
        {
            var current = JsonNode.Parse(File.ReadAllText(AuthPath));
            var tokens = current?["tokens"];
            if (tokens is null) return;
            if ((string?)tokens["refresh_token"] != usedRefresh)
            {
                Log.Write("[codex] write-back skipped: file changed by another process");
                return;
            }
            tokens["access_token"] = newAccess;
            if (!string.IsNullOrEmpty(newRefresh)) tokens["refresh_token"] = newRefresh;
            if (!string.IsNullOrEmpty(newId)) tokens["id_token"] = newId;
            current!["last_refresh"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

            File.Copy(AuthPath, AuthPath + ".bak", true);
            var tmp = AuthPath + ".tmp";
            File.WriteAllText(tmp, current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(tmp, AuthPath, true);
            Log.Write("[codex] token refreshed and written back");
        }
        catch (Exception ex)
        {
            Log.Write("[codex] write-back error: " + ex.Message);
        }
    }
}

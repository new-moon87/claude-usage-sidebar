# ClaudeSidebar 개발 명세서 — 원샷 구현용

> **사용법**: 이 문서 전체를 Claude(또는 다른 AI 코딩 도구)에 붙여넣고 "이 명세대로 구현해줘"라고 요청한다.
> 여기 적힌 내용은 전부 실제 구현·운영에서 **검증된 사실**이며, 시행착오 끝에 확정된 함정 회피책이 포함되어 있다.
> 문서와 다르게 구현하고 싶은 부분이 없다면 임의로 바꾸지 말 것 — 특히 "함정" 표시가 붙은 항목은 어기면 반드시 문제가 난다.

## 0. 한 줄 정의

Claude Code 와 Codex 의 사용량 한도를 화면 오른쪽 가장자리에 폭 13px 알약으로 표시하는 Windows 상주 앱.
둘 중 하나라도 실행 중일 때만 나타난다.

## 1. 확정 요구사항

### 표시 지표 (알약 5개, 위에서부터)

| 약자 | 제품 | 지표 | 고유 색 |
|---|---|---|---|
| CH | Claude | 5시간 세션 한도 | `#7F77DD` (보라) |
| CW | Claude | 주간 한도 (전체 모델) | `#EF9F27` (주황) |
| C+ | Claude | 주간 한도 (모델 전용 — `C` + API가 주는 `display_name` 첫 글자, 예: `CF`) | `#378ADD` (파랑) |
| GH | Codex | 24시간 이하 창 중 사용률 최대 (3.6, 함정 ⑯) | `#10A37F` (초록) |
| GW | Codex | 24시간 초과 창 중 사용률 최대 | `#19C39C` (밝은 초록) |

- 앞 글자가 제품(`C`=Claude, `G`=GPT/Codex), 뒷 글자가 한도 종류다. 색은 보조 단서. 그룹 사이만 여백을 벌린다.
- 크레딧은 양쪽 모두 알약이 아니라 상세 패널의 한 줄 텍스트다.

- 사용률 85% 초과 시 해당 알약이 위험색 `#E24B4A`로 바뀌고 Opacity 1.0↔0.5 (700ms, AutoReverse, Forever) 펄스.
- 알약 안: 맨 위에 약자 2글자, 그 아래 % 숫자를 **한 자리씩 세로로 쌓아** 표시 (`string.Join("\n", v.ToString().ToCharArray())`).
- 알약은 세로 미니 게이지: 사용률만큼 아래에서 고유 색이 차오른다.

> **함정 ⑱ (알약 안에서는 전부 세로로 쌓는다)** — 알약 폭은 13px 뿐이라 두 글자를 가로로 늘어놓을 수 없다.
> `CW` 를 가로로 넣으려면 글자를 6 까지 줄여야 하는데 그러면 읽을 수가 없다(실제로 해 보고 되돌린 경로다).
> **라벨도 숫자도 한 줄에 한 글자씩 세로로 쌓고 크기는 8.5 를 유지한다.** 그러면 폭은 제약에서 빠지고
> 높이만 남는다: 라벨 2줄 + 숫자 3줄(`100`) = 5줄 × 줄높이 9.5 + 위 여백 3 ≈ 51 < 64 로 들어간다.
> `LineStackingStrategy.BlockLineHeight` 를 같이 줘야 줄높이가 실제로 먹는다.
> 크기를 올릴 일이 생기면 **값을 100 으로 물린 화면을 캡처해서** 마지막 자리가 아래 둥근 끝을 파고드는지 볼 것.

### 동작

- Claude 실행 감지 → 슬라이드바 페이드 인(180ms), 종료 감지 → 페이드 아웃(150ms).
- 알약에 마우스 올림 → 왼쪽으로 상세 패널 펼침. 벗어나면 350ms 후 접힘.
- 상세 패널: 지표명 · % · 게이지 바 · 리셋 시각 · 푸터(갱신 시각 HH:mm:ss + 새로고침 + 핀 버튼) · 오류 상태 줄.
- 핀 버튼(Segoe MDL2 Assets 글리프)으로 펼침 고정/해제. **알약 클릭으로는 고정하지 않는다** (드래그와 혼동됨 — 실사용 피드백으로 제거된 설계).
- 알약 드래그 → 위아래 위치 이동(저장). 가로는 항상 화면 오른쪽에 스냅.
- **Claude 창이 떠 있는 모니터로 자동 이동** — Claude 를 다른 화면으로 옮기면 사이드바도 따라간다(5장 함정 ⑮).
- 트레이 아이콘 메뉴: Claude 재로그인(터미널 열기) / 다운로드 사이트 열기 / 항상 표시 / Windows 시작 시 실행 / 버전·카피라이트(비활성 캡션) / 종료. 새로고침은 트레이 아이콘 더블클릭.

## 2. 기술 스택

- **C# / .NET 8 WPF** + WinForms(트레이 NotifyIcon 용). **NuGet 의존성 0개.**
- 빌드에 .NET 8 SDK 필요: `winget install Microsoft.DotNet.SDK.8`
- 실행에 .NET 8 Desktop Runtime(`Microsoft.WindowsDesktop.App 8.x`) 필요.

### csproj 전문 — 그대로 사용할 것

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>ClaudeSidebar</AssemblyName>
    <RootNamespace>ClaudeSidebar</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
  </PropertyGroup>

  <ItemGroup>
    <Using Remove="System.Drawing" />
    <Using Remove="System.Windows.Forms" />
  </ItemGroup>

</Project>
```

> **함정 ①** — `UseWPF` + `UseWindowsForms`를 같이 켜면 SDK가 `System.Drawing`·`System.Windows.Forms` 전역 using을
> 주입해 `Application`/`Color`/`Rectangle`이 전부 CS0104 모호성 오류가 난다. 위의 `<Using Remove>` 2줄이 해결책이다.
> WinForms 타입은 트레이 파일에서만 `using WF = System.Windows.Forms;` 별칭으로 쓴다.

`app.manifest`에는 `PerMonitorV2` dpiAwareness 선언 (200% 스케일 모니터에서 배치가 어긋나지 않게).

## 3. 데이터 계약 — 전부 실측 검증됨

### 3.1 자격 증명 파일 (읽기 + 갱신 시 write-back)

`%USERPROFILE%\.claude\.credentials.json`:

```jsonc
{
  "claudeAiOauth": {
    "accessToken": "...",           // 수명 8시간
    "refreshToken": "...",          // 수명 30일, 갱신할 때마다 회전 + 30일 재연장
    "expiresAt": 0,                 // epoch ms
    "refreshTokenExpiresAt": 0,
    "scopes": ["user:inference", "..."],
    "subscriptionType": "...",
    "rateLimitTier": "..."
  },
  "mcpOAuth": { }                   // 다른 소유자의 데이터 — 절대 훼손 금지
}
```

> **함정 ②** — 파일에는 `claudeAiOauth` 말고 다른 키(`mcpOAuth` 등)도 있다. write-back은 반드시
> **`JsonNode`로 전체를 파싱해 필요한 값만 바꾼 뒤 통째로 다시 직렬화**한다 (모르는 키 보존).
> 저장은 임시 파일에 쓰고 `File.Move(tmp, path, overwrite:true)`로 원자 교체. 교체 직전 `.bak` 백업 1개 생성.
> 인코딩은 **UTF-8 BOM 없이** (`new UTF8Encoding(false)`) — BOM이 붙으면 Claude Code(Node)의 JSON.parse가 깨진다.

> **함정 ③ (경합)** — npm CLI 등 다른 프로세스가 같은 파일을 갱신할 수 있다. write-back 직전에 파일을 다시 읽어
> `refreshToken`이 내가 갱신에 사용한 값과 다르면 **내 결과를 버리고 파일 쪽을 우선**한다 (회전된 토큰을 덮어쓰면 로그인이 깨진다).

이 파일이 없는 PC = Claude Code 로그인 이력 없음 → 상태 문구 "Claude 로그인 필요" 표시.

### 3.2 토큰 갱신

```
POST https://console.anthropic.com/v1/oauth/token
Content-Type: application/json
{ "grant_type": "refresh_token", "refresh_token": "<값>", "client_id": "9d1c250a-e61b-44d9-88ed-5944d1962f5e" }
```

- `client_id`는 Claude Code에 내장된 공개 값이다.
- 응답: `access_token`, `refresh_token`(회전됨), `expires_in`(초). 새 `expiresAt = now_ms + expires_in*1000`.
- 갱신 시점: `expiresAt - 5분`이 지났을 때만. 유효하면 네트워크 호출 없이 파일의 accessToken을 그대로 쓴다.

> **함정 ④ (레이트리밋)** — 이 엔드포인트는 **HTTP 429를 자주 반환**하며, 한 번 걸리면 수 분간 지속된다.
> 429 수신 → 5분간 갱신 재시도 금지(만료 토큰이라도 일단 반환해 폴백 경로로 흐르게 함).
> 4xx 실패(리프레시 토큰 사망) → 10분간 갱신 재시도 금지 + "재로그인 필요 (HTTP n)" 상태. 단, 사용자가 재로그인하면
> 파일에 신선한 토큰이 생기므로 매 호출마다 파일을 새로 읽는 구조면 백오프 중에도 자동 복구된다.

### 3.3 사용량 API — 응답 구조가 직관과 다름 (가장 중요)

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>
anthropic-beta: oauth-2025-04-20
```

응답에서 실제로 쓰는 부분과 매핑 (그 외 필드는 전부 무시):

| 지표 | 위치 |
|---|---|
| CH | 최상위 `five_hour: {utilization, resets_at}` |
| CW | 최상위 `seven_day: {utilization, resets_at}` |
| CF | **`limits[]` 배열에서 `kind == "weekly_scoped"`인 항목** → `percent`, `resets_at`, `scope.model.display_name`(예: "Fable") |
| 크레딧 | `spend` 객체 → `percent`, `used.amount_minor`, `limit.amount_minor`, `exponent`(센트 단위), `enabled`, `disabled_reason`. 없으면 `extra_usage.utilization` 폴백 |

> **함정 ⑤** — 최상위에 `seven_day_opus`, `seven_day_sonnet` 같은 그럴듯한 필드가 있지만 **전부 null**이다
> (구버전 잔재). 모델 전용 주간 한도는 `limits[]` 안에만 있다. 그래도 방어적으로 `limits[]`에서 못 찾으면
> `seven_day_fable/opus/sonnet` 순서로 최상위도 확인하는 폴백을 넣는다.
> 응답에는 미공개 기능의 코드네임 필드들도 섞여 있으므로, **모르는 필드는 조용히 무시**하는 관대한 파서가 필수다.
> 응답 원문을 저장소에 커밋하지 말 것 (개인 사용량 + 미공개 필드명 노출).

> **함정 ⑥ (레이트리밋)** — 이 엔드포인트도 짧은 간격 반복 호출 시 429가 뜨고 **수 분간 지속**된다. 60초 폴링은 안전.
> 대책: ⑴ 수동 새로고침은 5초에 1회로 쓰로틀 ⑵ 429 수신 시 3분간 폴링 중단 ⑶ 상태 문구 "요청이 너무 잦음 · 잠시 후 자동 갱신"
> ⑷ 실패해도 마지막 성공 데이터를 화면에 유지.

401 처리: 강제 갱신 1회 후 재시도. 그래도 실패하면 폴백 소스로.

### 3.4 폴백 소스 — 데스크톱 앱의 자체 기록

`%APPDATA%\Claude\plan-usage-history.json` — 데스크톱 앱이 **5분 간격**으로 기록:

```jsonc
{ "version": 2, "samples": [ { "t": 0 /* epoch ms */, "org": "...", "u": { "fh": 0, "sd": 0, "xu": 0 } } ] }
```

- `fh`=5시간%, `sd`=주간%, `xu`=추가 사용량%(간헐적으로만 존재). **F 지표와 리셋 시각은 없다.**
- 마지막 샘플만 읽는다. 읽기는 `FileShare.ReadWrite | FileShare.Delete`로 열 것 (앱이 쓰는 중일 수 있음).
- 용도: 앱 시작 직후 첫 화면 즉시 표시 + API 실패 시 폴백. API 스냅샷을 폴백 데이터로 덮어쓰지 말 것
  (마지막 데이터가 FILE 소스일 때만 교체).

### 3.5 프로세스 감지

- 3초 간격으로 `claude`, `codex` 두 이름을 `Process.GetProcessesByName` — 결과의 각 `Process`를 반드시 `Dispose()`.
  **둘 중 하나라도 돌면** 사이드바를 띄운다. 따라가기도 같은 pid 집합을 쓴다.
- 데스크톱 앱 엔진이 `claude.exe`라서 이걸로 충분하다. npm CLI는 node로 돌아 오탐 없음.
- 자기 자신은 `ClaudeSidebar.exe`로 명명해 충돌 회피.
- 같은 3초 틱에서 **Claude 창이 어느 모니터에 있는지**도 구한다(사이드바 따라가기용).
  포커스가 Claude 프로세스에 있으면 `GetForegroundWindow()` 의 창을, 아니면 `MainWindowHandle` 이 있는 창을 고른 뒤
  `MonitorFromWindow` 로 장치명을 얻는다. 언제나 프로세스를 다시 열거하므로 별도 타이머는 필요 없다.
- 최소화·숨김 창은 반드시 걸러낼 것(`IsIconic` / `IsWindowVisible`). 최소화 창의 좌표는 `(-32000,-32000)` 이라
  모니터 판정이 엉뚱한 곳으로 나온다.
- 쓸 만한 창을 못 찾으면 **마지막 값을 유지**한다. 잠시 다른 앱을 쓴다고 사이드바가 되돌아가면 안 된다.

### 3.6 Codex 사용량 — 소스 두 개

Claude 와 완전히 독립이다. 한쪽이 죽어도 다른 쪽은 그대로 보여야 하므로 스냅샷·상태·백오프를 따로 둔다.

**자격 증명** `~/.codex/auth.json` (실측 구조)

```json
{ "auth_mode": "chatgpt", "OPENAI_API_KEY": null,
  "tokens": { "id_token": "...", "access_token": "...", "refresh_token": "...", "account_id": "..." },
  "last_refresh": "2026-09-14T07:21:08.000000Z" }
```

만료 시각이 파일에 없다(JWT 안에만 있다). 그러니 유효기간을 추측하지 말고 **그냥 써 본 뒤 401 이 오면 한 번 갱신**한다.
갱신은 `POST https://auth.openai.com/oauth/token`, 본문
`{client_id, grant_type:"refresh_token", refresh_token, scope:"openid profile email"}`.
`client_id` 는 `app_EMoamEEZ73f0CkXaXp7hrann` — Codex CLI 바이너리에 박혀 배포되는 공개값이라 비밀이 아니다.
write-back 은 3.1 과 같은 불변식(미지 키 보존 · 원자적 교체 · `.bak` · 경합 감지)을 그대로 지킨다.

**API** `GET https://chatgpt.com/backend-api/codex/usage` (`/wham/usage` 도 같은 응답을 준다)

```json
{ "plan_type": "prolite",
  "rate_limit": { "primary_window": { "used_percent": 3, "limit_window_seconds": 604800, "reset_at": 1789971133 },
                  "secondary_window": null },
  "code_review_rate_limit": null,
  "additional_rate_limits": [
    { "limit_name": "GPT-5.3-Codex-Spark",
      "rate_limit": { "primary_window": { "used_percent": 0, "limit_window_seconds": 18000, "reset_at": ... },
                      "secondary_window": { "used_percent": 0, "limit_window_seconds": 604800, "reset_at": ... } } } ],
  "credits": { "has_credits": false, "unlimited": false, "balance": "0" } }
```

**폴백** `~/.codex/sessions/<년>/<월>/<일>/rollout-*.jsonl` 의 `payload.type == "token_count"` 줄

```json
"rate_limits": { "limit_id": "codex", "plan_type": "plus",
  "primary":   { "used_percent": 2.0, "window_minutes": 300,   "resets_at": 1779083125 },
  "secondary": { "used_percent": 0.0, "window_minutes": 10080, "resets_at": 1779669925 },
  "credits":   { "has_credits": false, "unlimited": false, "balance": null } }
```

가장 최근 파일 하나만, **뒤에서부터** 훑어 첫 `token_count` 에서 멈춘다. `FileShare.ReadWrite | Delete` 로 열 것.
이 기록은 Codex CLI 가 돌 때 실시간으로 쌓인다 — 즉 **사용량이 실제로 변하는 순간에는 항상 최신**이고,
Codex 를 안 쓰는 동안에만 낡는다. 낡은 값을 최신인 척 보여주면 안 되므로 소스와 경과일을 화면에 같이 띄운다.
(데스크톱 앱의 `thread_history_1.sqlite` 에는 한도가 아예 없다 — 실측 0건. 거기서 찾지 말 것.)

> **함정 ⑯ (창 이름이 창 길이를 뜻하지 않는다)** — `primary_window` 를 5시간, `secondary_window` 를 주간으로
> 짐작하면 틀린다. 실측: prolite 계정은 `primary_window` 가 **604800초(7일)** 이고 `secondary_window` 는 null 이며,
> 5시간(18000초) 창은 `additional_rate_limits[]` 안 모델별 한도에 들어 있다. 무료 계정 기록에서는 43200분(30일)도 나왔다.
> **반드시 `limit_window_seconds`(파일은 `window_minutes`) 값으로 분류할 것.** 이 앱은 24시간을 경계로 갈라
> 각 그룹에서 **사용률이 가장 높은 창**을 알약에 쓴다 — 먼저 막히는 한도가 사용자에게 의미 있는 값이다.
> 어느 창이든 null 일 수 있고, `credits.balance` 는 숫자가 아니라 **문자열**("0")로 온다.

> **함정 ⑰ (앞단 봇 완화 — .NET 은 이 엔드포인트를 못 뚫는다)** — 사용량 엔드포인트 앞에는 봇 완화가 붙어 있고,
> 막히면 401/403 JSON 이 아니라 **10KB짜리 HTML 차단 페이지**가 온다.
>
> 실측 결과는 이렇다. 7분간 아무 요청도 보내지 않은 뒤 한 번씩 쐈을 때:
>
> | 클라이언트 | 결과 |
> |---|---|
> | python urllib (OpenSSL) | **200** |
> | .NET 8 `HttpClient` (SChannel) | 403 |
> | Windows `curl.exe` (SChannel) | 403 |
> | node `fetch` (OpenSSL + 브라우저풍 헤더) | 403 |
>
> .NET 쪽은 헤더를 파이썬과 똑같이 맞춰도, ALPN 광고를 빼도(`ConnectCallback` + `SslClientAuthenticationOptions`),
> TLS 1.2 로 고정해도, HTTP/2 를 강제해도 전부 403 이었다. **Windows 의 TLS 스택으로는 통과할 방법이 없다.**
> 그러니 이 앱에서 Codex 사용량의 **정상 경로는 rollout 기록 파일**이고, API 호출은 "되면 좋은" 부가 경로다.
> 코드는 남겨 둔다 — 비용이 없고, 다른 환경이나 정책 변경에서는 통과할 수 있다.
>
> 구현 규칙:
> - **User-Agent 를 반드시 붙일 것**(`codex_cli_rs/<버전>`). 없으면 확실히 403 이다. 토큰 갱신 요청에도 붙인다.
>   (토큰 갱신 호스트 `auth.openai.com` 은 SChannel 에서도 정상 응답한다 — 막히는 건 `chatgpt.com` 쪽뿐이다.)
> - **사용량 폴링(60초)에 Codex 를 끼워 넣지 말 것.** 최소 10분 간격, 수동 새로고침도 60초 간격으로 제한한다.
>   몇 분 사이에 스무 번쯤 두드리면 파이썬까지 포함해 전부 몇 분간 막힌다.
> - 403 은 사용자가 손쓸 수 없는 상태다. **빨간 오류 줄을 띄우지 말고** 조용히 기록 소스로 내려간 뒤
>   6시간 쉰다. 출처는 패널 머리글의 "기록" 표시로 드러낸다. 429 만 30분 백오프 + 안내 문구.

## 4. 프로젝트 구조

```
src/ClaudeSidebar/
├─ ClaudeSidebar.csproj          (2장 전문 그대로)
├─ app.manifest                  (PerMonitorV2)
├─ App.xaml / App.xaml.cs        — 오케스트레이션: Mutex 단일 인스턴스, 타이머, 표시/숨김, RefreshAsync
├─ MainWindow.xaml(.cs)          — 사이드바 뷰 전체 (알약·패널을 코드로 생성)
├─ Models.cs                     — UsageBucket(record), UsageSnapshot, AppSettings
├─ Log.cs                        — %APPDATA%\ClaudeSidebar\log.txt (512KB 넘으면 삭제 후 재생성)
├─ TrayIcon.cs                   — WinForms NotifyIcon (아이콘은 16x16 Bitmap을 코드로 그려 생성)
└─ Services/
   ├─ CredentialStore.cs         — 3.1~3.2 (토큰 관리 전부)
   ├─ UsageApiClient.cs          — 3.3 (JsonDocument 관대 파싱)
   ├─ UsageHistoryReader.cs      — 3.4
   ├─ CodexCredentialStore.cs    — 3.6 (Codex 토큰 관리)
   ├─ CodexUsageClient.cs        — 3.6 (사용량 API)
   ├─ CodexHistoryReader.cs      — 3.6 (rollout 폴백)
   ├─ ProcessWatcher.cs          — 3.5
   ├─ DisplayInfo.cs             — 모니터 열거/DPI/물리 좌표 (캐시 금지)
   ├─ SettingsStore.cs           — settings.json {MonitorName, PhysY, Pinned, Autostart, ForceShow}
   │                                로드 실패를 조용히 삼키지 말고 로그를 남길 것
   └─ Autostart.cs               — HKCU\...\Run에 "ClaudeSidebar"=현재 exe 경로
```

HttpClient는 하나를 공유 (`Timeout = 20초`).

> **함정 ⑦ (보안 불변식)** — 토큰 값은 로그·UI·예외 메시지 어디에도 출력 금지.
> `api.anthropic.com`/`console.anthropic.com` 외에는 아무것도 전송하지 않는다.

## 5. 창 구현 — WPF 함정 밀집 구역

### 창 구성

```xml
<Window WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        Topmost="True" ShowInTaskbar="False" ResizeMode="NoResize"
        Width="232" Height="320" ShowActivated="False">
  <StackPanel x:Name="RootPanel" Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
    <Border x:Name="DetailPanel" Width="202" CornerRadius="10" Background="#F51F1E1D"
            BorderBrush="#26FFFFFF" BorderThickness="1" Margin="0,0,6,0"
            Padding="12,8,12,8" Visibility="Hidden" VerticalAlignment="Center">
      <StackPanel x:Name="DetailRows"/>
    </Border>
    <StackPanel x:Name="PillStrip" Margin="0,0,3,0" VerticalAlignment="Center"/>
  </StackPanel>
</Window>
```

> **함정 ⑧** — `SizeToContent`를 쓰지 말 것 (AllowsTransparency와 조합 시 첫 레이아웃 클리핑 버그 + 확장 때마다
> 재배치 연쇄). **창 크기를 260×320으로 고정**하고, 패널은 `Visibility.Hidden`(Collapsed 아님)으로 접는다 —
> 레이아웃 공간이 유지되어 창 크기가 안 변하고, AllowsTransparency 창에서 알파 0 영역은 클릭이 그대로 통과한다.

- 배치: **`SystemParameters.WorkArea`를 쓰지 말 것.** 이 값은 주 모니터의 작업영역만 돌려주므로 보조 모니터에
  도달할 수 없고, 혼합 DPI에서 위치와 크기의 기준 배율이 어긋난다. 대신 `EnumDisplayMonitors`로 매번 새로 열거하고,
  `GetDpiForMonitor`로 배율을 얻어 **물리 픽셀**로 계산한다 (`Services/DisplayInfo.cs`).

> **함정 ⑪ (모니터 정보 캐시 금지)** — 모니터 목록·작업영역을 시작 시 한 번만 읽어 캐시하면, 모니터를 꽂거나 빼거나
> 배율을 바꿨을 때 옛 좌표에 창을 놓는다. 매 배치마다 새로 열거할 것. 그리고 `SystemEvents.DisplaySettingsChanged`를
> **실제로 구독**할 것(상수만 선언하고 처리기가 비어 있는 사례가 흔하다). 이 신호는 구성이 확정되기 **전에** 오므로
> 한 번만 반응하면 중간 상태를 잡는다 → **0.15 / 0.7 / 2 / 5초 뒤 다시 확인해 마지막 값을 쓴다.**
> 모니터 전원을 껐다 켜는 경로는 이벤트가 아예 안 오기도 하므로, **2초 주기로 구성 지문과 실제 창 좌표를 대조하는
> 그물**을 따로 둔다.

> **함정 ⑫ (작업영역의 위치를 잃지 말 것)** — 보조 모니터는 원점에 있지 않다(실측 예: `(-182,1080)-(2698,2880)`).
> "오른쪽 끝"을 화면 **폭**으로 계산하면 주 모니터 기준 좌표가 나와 화면 밖에 놓인다. 반드시 `work.Right - 창물리폭`
> 처럼 **실제 변 좌표**로 계산한다. 폭 × 비율로 하면 와이드 모니터에서 가장자리가 아니라 화면 한가운데에 붙는다.

> **함정 ⑬ (크기를 SetWindowPos로 건드리지 말 것)** — 실측 재현: 192dpi 모니터로 옮기며 물리 `464x640`을 지정했더니
> 실제로는 **`928x1280`**(정확히 2배)이 되어 창이 화면 밖으로 나갔다. WPF가 넘겨받은 물리값을 자기 논리 좌표(DIP)로
> 착각해 모니터 배율만큼 다시 부풀리기 때문이다. 증상은 "창은 그 자리에 있는데 안 보이고, 마우스를 올리거나
> 크기가 바뀌면 나타난다".
> **해법: 위치만 `SWP_NOSIZE`로 옮기고 크기는 WPF 논리값(260x320)에 맡긴다.** 그리고 DPI 경계를 넘으면 물리 크기가
> 바뀌므로 **적용 → `GetWindowRect` 대조 → 재계산**을 최대 3회 돌린다(1회로는 전환 지연을 못 흡수한다).
> 논리 크기가 오염됐으면(`Width`가 520 등) 먼저 원래 값으로 되돌린 뒤 배치한다.

> **함정 ⑮ (Claude 화면 따라가기)** — "Claude 가 떠 있는 모니터로 이동"은 두 가지를 틀리기 쉽다.
> **첫째, 매번 Claude 모니터로 끌고 가면 안 된다.** 배치 그물(2초)이 같은 판정을 계속 돌리므로,
> 사용자가 다른 모니터로 끌어다 놓은 창이 2초마다 다시 끌려간다. **모니터가 바뀌는 순간에만** 반응하고
> 직전에 따라간 모니터를 기억해 둬서 같은 값이면 아무 것도 하지 않는다 (드래그 위치를 보존하는 유일한 방법).
> **둘째, 표시보다 먼저 목표 모니터를 잡아야 한다.** 순서가 바뀌면 저장된 모니터에 한 번 떴다가 건너뛰는 게
> 눈에 보인다(실측 확인됨). 워처의 실행 상태 변경 처리에서 **따라가기 → 표시/숨김** 순으로 부를 것.
> 모니터를 옮길 때는 저장된 세로 위치를 버리고 새 모니터 가운데로 잡는다. 따라간 위치는 **저장하지 않는다**
> — settings.json 의 모니터 값은 사용자가 드래그로 고른 것만 담아야 의미가 산다.
> 검증 로그: `[follow] Claude 창 모니터 = \\.\DISPLAY1` 다음 `[follow] 사이드바 이동 ...` 다음
> `[place:follow] ... 일치` 세 줄이 순서대로 찍히는지 본다.

- `OnSourceInitialized`에서 `GWL_EXSTYLE(-20)`에 `WS_EX_TOOLWINDOW(0x80) | WS_EX_NOACTIVATE(0x08000000)` OR —
  Alt-Tab에서 숨기고 포커스를 훔치지 않게 (마우스 이벤트는 정상 동작).
- 호버: `RootPanel.MouseEnter` → 패널 Visible / `MouseLeave` → 350ms 타이머 후 `!Pinned && !RootPanel.IsMouseOver`면 Hidden.
- 드래그: `PillStrip.MouseLeftButtonDown`에서 `DragMove()` 호출 후 **X를 오른쪽 가장자리로 되돌리고** Top만 클램프·저장.
  DragMove는 try/catch로 감싼다 (마우스를 이미 뗀 타이밍이면 InvalidOperationException).

### 알약 (코드로 생성, 지표당 1개)

- `Grid` 13×64, `Clip = new RectangleGeometry(new Rect(0,0,13,64), 6.5, 6.5)` — 라운드 클리핑은 이 방법뿐.
- 레이어 순서: 트랙 `Rectangle`(`#C81F1E1D` — 짙은 반투명이라 **흰 화면에서도 보임**, 초기에 흰색 계열로 했다가 실패한 부분)
  → 게이지 `Rectangle`(VerticalAlignment=Bottom, `Height = 64 * clamp(pct,0,100)/100`)
  → 텍스트 StackPanel(약자 + 세로 숫자, FontSize 8.5, Bold, 흰색 계열, `LineHeight 9.5` + `BlockLineHeight`).
- 감싸는 `Border`: `CornerRadius 7.5`, `BorderBrush #38FFFFFF` 두께 1, `Background = Brushes.Transparent`(히트테스트용), 상하 Margin 3.

### 핀 버튼

```csharp
_pinBtn.Text = Pinned ? "\uE77A" : "\uE718";   // Segoe MDL2 Assets: Unpin / Pin
```

> **함정 ⑨** — 글리프를 리터럴 문자로 소스에 박으면 도구·인코딩에 따라 유실된다. 반드시 `"\uE77A"`/`"\uE718"` 이스케이프로 쓸 것.
> `FontFamily = new FontFamily("Segoe MDL2 Assets")` (Windows 기본 내장).

### 새로고침 피드백 (실사용 피드백으로 추가된 필수 요건)

> **함정 ⑩ (UX)** — 새로고침을 눌러도 반응이 안 보이면 사용자가 연타하고, 연타는 429를 부른다. 반드시:
> ⑴ 클릭 즉시 푸터를 "갱신 중…"으로 ⑵ 완료 시 **초 단위 시각** "HH:mm:ss 갱신 · API|앱 기록" ⑶ 쓰로틀에 걸리면
> "5초에 한 번만 갱신돼요"를 1.3초 표시 후 원상 복구.

상태 줄(빨강, 오류 시만 표시): 문구에 "로그인"이 포함되면 밑줄 + 클릭 시 재로그인 터미널 열기.

## 6. 트레이와 재로그인

- 트레이 아이콘: `System.Drawing.Bitmap` 16×16에 세로 막대 3개(보라/주황/파랑)를 그려 `Icon.FromHandle(bmp.GetHicon())`.
- "Claude 재로그인": `cmd /k title Claude 로그인 & echo 로그인 화면이 안 나오면 /login 을 입력해 주세요. & "<cli경로>" /login`
- CLI 경로 탐색 3단계: ⑴ PATH에서 `claude.cmd|exe|bat` ⑵ `%APPDATA%\Claude\claude-code\<최신 폴더>\claude.exe`
  (데스크톱 앱 내장 엔진 — **CLI 미설치 PC 대응**) ⑶ `%USERPROFILE%\.local\bin\claude.exe`. 전부 없으면 설치 안내 MessageBox.
- 로그인 완료 후엔 매 폴링마다 credentials 파일을 새로 읽으므로 최대 60초 안에 자동 복구된다.

## 7. 오케스트레이션 (App.xaml.cs)

- `ShutdownMode="OnExplicitShutdown"`, Mutex `"ClaudeSidebar_SingleInstance"`로 중복 실행 차단.

> **함정 ⑭ (자기 교체와 단일 인스턴스 뮤텍스)** — 자동 업데이트가 새 exe 를 띄우기 전에 뮤텍스를
> **`ReleaseMutex()` 만 하면 안 된다.** 그것은 소유권만 놓을 뿐이고, 이름 있는 뮤텍스는 핸들이 전부
> 닫혀야 사라진다. 핸들을 쥔 채 새 프로세스를 띄우면 그쪽은 `createdNew == false` 를 보고
> **로그 한 줄 남기지 않고 종료**한다 — 사용자에게는 "업데이트하더니 앱이 사라졌다"로 보인다.
> 구 프로세스가 먼저 죽으면 우연히 성공하므로 몇 번은 멀쩡히 넘어가다가 어느 날 걸린다
> (실제로 0.1.3.0 → 0.1.4.0 에서 걸렸다).
> **`ReleaseMutex()` → `Dispose()` → `null` 까지 한 뒤에 새 exe 를 띄운다.**
> 검증은 "업데이트 후 새 버전의 `=== app start ===` 가 로그에 찍히고 프로세스가 살아 있는가"로 한다.
- 타이머: 프로세스 감지 3초 / 사용량 폴링 60초(표시 중일 때만) / 카운트다운 재계산 30초 / **배치 그물 2초**(구성 지문 + 실제 좌표 대조).
- `SystemEvents.PowerModeChanged` Resume 시 즉시 갱신 + 재배치 (`Dispatcher.Invoke`로 감쌀 것).
- `SystemEvents.DisplaySettingsChanged` 구독 → 함정 ⑪의 다단 재확인 경로로 보낼 것.
- 시작 시퀀스: 설정 로드 → 폴백 파일로 첫 화면 즉시 표시 → 워처 시작 → 강제 갱신 1회.
- 따라가기 호출은 두 군데뿐이다: 워처의 실행 상태 변경 처리(표시보다 먼저)와 2초 배치 그물 맨 앞. 함정 ⑮ 참조.
- `RefreshAsync`: `_fetching` 가드(재진입 금지) → 429 백오프 중이면 즉시 반환 → 토큰 확보 → API → 실패 시 폴백 →
  `finally`에서 항상 `ApplySnapshot` + 로그 1줄 (`src=API H=.. W=.. F=.. C=.. status=..` — 이 로그가 검증 수단이다).
- `RefreshCodexAsync`: Claude 와 같은 모양이되 **자체 백오프와 자체 상태 문자열**을 쓴다(함정 ⑰).
  상태 줄은 두 제품 몫을 줄바꿈으로 합쳐 보여주고, 각 메시지에 제품명을 붙인다 —
  재로그인 클릭은 Claude 터미널을 여는 동작이라 Codex 문제일 때 눌리면 안 된다.
- 자동 시작: 설정 Autostart=true(기본)면 시작 시마다 현재 exe 경로로 HKCU Run 키 갱신.

리셋 시각 포맷: 5시간 → "리셋까지 N시간 M분"(1시간 미만이면 "M분"), 주간 → `ko-KR` 요일 + "HH:mm 리셋".

## 8. 빌드·배포

```bash
dotnet publish src/ClaudeSidebar/ClaudeSidebar.csproj -c Release -o dist
```

- framework-dependent 산출물 약 220KB (Desktop Runtime 필요). self-contained 단일 exe는 약 138MB
  (`-r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`).
- 재배포 시 실행 중인 프로세스를 먼저 종료해야 파일 잠금이 풀린다: `Stop-Process -Name ClaudeSidebar -Force`.
- (선택) npm 런처: `package.json`의 `bin` + `bin/cli.js`가 `install|start|uninstall` 서브커맨드 제공 —
  dist를 `%LOCALAPPDATA%\ClaudeSidebar\`로 복사·실행·제거. `npx github:<owner>/<repo> install` 형태로 사용.

## 9. 검증 체크리스트

`%APPDATA%\ClaudeSidebar\log.txt`로 확인한다 (PowerShell에서 읽을 땐 `-Encoding UTF8` — BOM 없는 UTF-8이라 지정 안 하면 한글이 깨져 보인다):

1. 앱 시작 → `claude running: True` → `refresh done: src=API H=.. W=.. F=.. C=.. status=` (4개 값 모두 숫자).
2. 만료된 accessToken 상태에서 시작해도 `token refreshed and written back` 후 API 수신 (사용자 개입 없음).
3. 새로고침 연타 → 실제 요청은 5초 1회, 429가 떠도 기존 값 유지 + 3분 후 자동 복구.
4. write-back 후 `.credentials.json`에 `mcpOAuth` 키가 그대로 남아 있고, Claude Code가 정상 동작.
5. Claude 데스크톱 앱 종료 → 수 초 내 사이드바 사라짐 / 재실행 → 다시 나타남.
6. 재부팅 → 트레이에 자동 상주 (HKCU Run 등록 확인).
7. 흰 배경 창 위에서 알약이 또렷하게 보임.
8. Claude 창을 보조 모니터로 옮김 → 수 초 내 사이드바가 그 화면 오른쪽 끝으로 이동하고, 되돌리면 따라서 돌아옴
   (혼합 DPI 간 이동이 핵심 — `[place:follow]` 마지막 줄이 `일치`여야 하고 실제 우변이 `work.Right` 와 같아야 한다).
9. 사이드바를 다른 모니터로 끌어다 놓은 뒤 Claude 를 그대로 두면, 2초 그물이 도는 동안에도 제자리에 머문다.
10. `codex refresh done: src=API h=.. w=.. plan=.. credit=..` 가 찍히고, Codex 알약 두 개에 값이 든다.
11. Codex 로그아웃 상태 / API 차단(403) 상태에서도 Claude 쪽 값은 멀쩡히 남고, Codex 만 조용히 기록 소스로 내려간다
    (403 에는 빨간 상태 줄이 뜨지 않아야 한다 — 함정 ⑰).
12. `~/.codex` 가 없는 PC 에서 Codex 알약 두 개와 상세 구역이 통째로 사라지고, 상태 줄에도 Codex 문구가 없다.
13. 창 높이 400 에서 상세 패널이 잘리지 않는다(알약 5개 + 크레딧 두 줄 + 상태 줄 두 줄까지).

## 10. 알려진 한계

- 사용량·갱신 엔드포인트는 **비공식**이며 예고 없이 바뀔 수 있다. 파서가 관대해서 지표가 `-`로 빠질 뿐 앱은 죽지 않는다.
- 알약 사이 6px 틈은 히트테스트가 비어 있어, 정확히 그 지점에 커서를 두면 패널이 안 펼쳐진다 (실사용 영향 미미).
- 폴백 모드에서는 F 지표·리셋 시각이 없다 (3.4 참고).
- Codex 사용량 API 는 Windows(.NET) 에서 앞단에 막힌다(함정 ⑰). 실질적으로 Codex 값은 CLI 기록 파일에서 온다 —
  Codex 를 쓰는 동안에는 최신이고, 한동안 안 쓰면 낡은 채로 "기록 N일 전" 과 함께 표시된다.
- Codex 토큰 갱신 경로(401 → refresh)는 실제 만료를 기다려야 밟히므로 아직 실측 검증되지 않았다.
  구조는 3.1 과 같고, 실패해도 기록 소스로 내려갈 뿐 Claude 쪽에는 영향이 없다.

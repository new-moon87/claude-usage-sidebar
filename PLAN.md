# Claude 사용량 사이드바 — 구현 계획

> 작성일: 2026-07-28 · 대상: 개인용 Windows 프로그램

## 1. 개요

PC 우측에 항상 위(topmost)로 떠 있는 소형 오버레이 사이드바.
Claude Code 데스크톱 앱이 **켜지면 자동으로 나타나고, 꺼지면 자동으로 사라진다.**

표시 지표 (4종):
1. **5시간 세션 한도** — 사용률 % + 리셋까지 남은 시간
2. **주간 한도(전체 모델)** — 사용률 % + 리셋 시각
3. **Fable 주간 한도** — 사용률 % (API 응답에 버킷이 있으면 표시)
4. **추가 사용량(Extra usage) 크레딧** — 사용률 % 또는 금액

기술 스택: **C# / .NET 8 WPF** + 트레이 아이콘 상주.
(WindowsDesktop 런타임 8.0.16 설치 확인됨 → framework-dependent 배포 가능. SDK만 설치 필요)

## 2. 사전 조사에서 확인된 사실 (2026-07-28)

| 항목 | 내용 |
|---|---|
| 감지 대상 프로세스 | `claude.exe` — `%APPDATA%\Claude\claude-code\<버전>\claude.exe` (앱 실행 중 다수 인스턴스) |
| 앱 자체 사용량 기록 | `%APPDATA%\Claude\plan-usage-history.json` — 앱이 **약 5분 간격**으로 기록. 샘플: `{t: epoch_ms, org: uuid, u: {fh: 5시간%, sd: 주간%, xu: 추가사용량%(간헐)}}`. **Fable 한도·크레딧 상세는 없음** |
| Claude Code 자격 증명 | `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth {accessToken, refreshToken, expiresAt, refreshTokenExpiresAt, scopes, subscriptionType, rateLimitTier}`. 액세스 토큰 수명 8시간 / 리프레시 토큰 30일이며, 갱신할 때마다 리프레시 토큰이 회전하고 30일이 다시 연장됨 |
| 사용량 API | `GET https://api.anthropic.com/api/oauth/usage` · 헤더 `Authorization: Bearer <token>`, `anthropic-beta: oauth-2025-04-20`. 만료 토큰으로 401 확인함. **응답의 정확한 필드 구성(Opus/Fable 버킷, extra usage)은 토큰 갱신 후 구현 1단계에서 확정** |
| 토큰 갱신 | `POST https://console.anthropic.com/v1/oauth/token` · body `{grant_type: "refresh_token", refresh_token: <값>, client_id: "9d1c250a-e61b-44d9-88ed-5944d1962f5e"}` (Claude Code 공개 client_id) |
| 데스크톱 앱 토큰 캐시 | `%APPDATA%\Claude\config.json`의 `oauth:tokenCacheV2` — safeStorage(DPAPI) 암호화 블롭이라 **사용하지 않음** |
| .NET | 런타임 8.0.16 있음(NETCore + WindowsDesktop) / **SDK 없음** → `winget install Microsoft.DotNet.SDK.8` |
| claude.ai 데스크톱 앱 | 미설치 (`%LOCALAPPDATA%\AnthropicClaude` 없음) — 감지 대상 아님 |

## 3. 데이터 소스 설계 (2중화)

### 주 소스 — OAuth 사용량 API (60초 폴링, 사이드바 표시 중일 때만)
- `.credentials.json`을 읽어 accessToken 사용, `expiresAt - 5분` 지나면 리프레시 후 사용.
- 리프레시로 받은 새 access/refresh 토큰은 **같은 JSON 형식으로 원자적 write-back** (임시 파일 + `File.Replace`, `.bak` 1개 유지).
- 파싱은 관대하게: 모르는 필드는 무시, 없는 버킷은 UI에서 행 자동 숨김.

### 보조 소스 — plan-usage-history.json (폴백 + 첫 화면 즉시 표시)
- `FileSystemWatcher`로 변경 감지, 마지막 샘플의 `fh/sd/xu` 사용.
- API 실패(네트워크·토큰 문제) 시에도 핵심 지표는 유지됨.

### 보안 원칙
- 토큰은 메모리에만 보관, 로그·화면에 절대 출력하지 않음.
- `api.anthropic.com` / `console.anthropic.com` 외 어디에도 전송하지 않음.
- credentials 파일 쓰기는 원자적 교체 + 백업. 쓰기 직전 파일을 다시 읽어, 파일의 refreshToken이 내가 갖고 있던 것과 다르면(npm CLI가 먼저 갱신한 경우) **내 갱신 결과를 버리고 파일 우선**.

## 4. 표시/숨김 로직

- 3초 간격으로 `Process.GetProcessesByName("claude")` 폴링 (경량).
- 자기 자신은 `ClaudeSidebar.exe`라 이름 충돌 없음. npm CLI는 node로 실행되므로 오탐 없음.
- 감지 → 슬라이드 인(200ms) + 사용량 폴링 시작 / 전부 종료 → 슬라이드 아웃 + 폴링 중단.
- 트레이 메뉴: 보이기/숨기기(수동 오버라이드), Windows 시작 시 자동 실행(HKCU Run 키), 새로고침, 종료.
- 사이드바 앱 자체는 항상 상주(트레이) — 그래야 Claude 실행을 감지해 자동으로 뜰 수 있음.

## 5. UI 설계 — 초소형 알약 스트립 + 호버 확장

### 기본 상태 (접힘) — 화면 오른쪽 가장자리에 붙는 폭 ~14px 스트립
- 지표당 **알약(pill) 1개**, 세로로 4개 (5시간 / 주간 전체 / Fable 주간 / 추가 사용량).
- 알약 폭 ~13px. 맨 위에 지표 약자 **H / W / F / C**, 그 아래 % 숫자를 **한 자리씩 세로로 쌓아** 표시.
- 알약 = 세로 미니 게이지: 사용률만큼 아래에서 색이 차오름.
- 지표별 고유 색 (예: 5시간=보라, 주간=주황, Fable=파랑, 추가 사용량=청록).
- 사용률 85% 초과 시 해당 알약이 **빨간색으로 전환 + 펄스 애니메이션**으로 경고.
- 화면을 거의 가리지 않는 것이 최우선 — 전체 높이 약 250px 내외.

### 호버 상태 (펼침)
- 알약 스트립에 마우스를 올리면 왼쪽으로 **상세 패널이 슬라이드 확장** (~200px):
  지표명 · % · 게이지 바 · 리셋 카운트다운(`resets_at` 기준) · 마지막 갱신 시각 · 새로고침 버튼 · 데이터 소스 상태 점(API/파일/오류).
- 마우스가 벗어나면 다시 알약만 남기고 접힘. **패널 하단의 핀 버튼**으로 펼침 고정/해제 (알약 클릭 고정은 혼동을 줘서 제거).
- 알약 배경은 짙은 반투명 + 얇은 테두리 → 흰 화면 위에서도 보임.
- 사용량 API 429 대책: 수동 새로고침 5초 쓰로틀 + 429 수신 시 3분 폴링 백오프.

### 공통
- 창: `WindowStyle=None` + `AllowsTransparency` + `Topmost` + `ShowInTaskbar=False`, 작업 영역(WorkArea) 기준 **우측 세로 중앙**.
- 다크 테마, 라운드 코너, 위/아래 드래그로 위치 조절(저장됨), 불투명도 설정 가능.
- 상세 패널 게이지 색: `<60%` 초록 → `60~85%` 주황 → `>85%` 빨강.

## 6. 프로젝트 구조

```
C:\AI\26 Claude_side_bar\
├─ PLAN.md                      ← 이 문서
└─ src\ClaudeSidebar\
   ├─ ClaudeSidebar.csproj      (net8.0-windows, UseWPF, 단일 exe publish)
   ├─ App.xaml / App.xaml.cs    — 트레이·프로세스 워처·서비스 초기화
   ├─ MainWindow.xaml(.cs)      — 사이드바 뷰
   ├─ ViewModels\UsageViewModel.cs
   ├─ Services\
   │   ├─ ProcessWatcher.cs     — claude.exe 감지
   │   ├─ CredentialStore.cs    — credentials 읽기/리프레시/원자적 write-back
   │   ├─ UsageApiClient.cs     — /api/oauth/usage 호출·파싱
   │   ├─ UsageHistoryReader.cs — plan-usage-history.json 읽기·감시
   │   └─ SettingsStore.cs      — %APPDATA%\ClaudeSidebar\settings.json
   └─ Assets\ (트레이 아이콘)
```

NuGet 의존성: `Hardcodet.NotifyIcon.Wpf`(트레이) 1개. 나머지는 BCL(System.Text.Json, HttpClient).

## 7. 구현 단계

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| 0. 준비 | .NET 8 SDK 설치 (`winget install Microsoft.DotNet.SDK.8`) | `dotnet --list-sdks` 출력 확인 |
| 1. 데이터 스파이크 ★ | 콘솔/스크립트로 토큰 리프레시 1회 → `/api/oauth/usage` 실제 응답 확보 → **Fable/Opus/extra usage 필드명 확정** → UI 항목 최종 결정 | 응답 JSON 샘플 저장, 필드 매핑표 작성 |
| 2. WPF 뼈대 | 프레임리스 창 + 트레이 + 프로세스 감지 표시/숨김 | Claude 켜면 뜨고 끄면 사라짐 |
| 3. 파일 소스 | plan-usage-history 연결 → fh/sd/xu 게이지 표시 | 앱 기록과 일치하는 % 표시 |
| 4. API 소스 | 리프레시 + 폴링 + write-back → Fable·크레딧 행 추가 | 4개 지표 + 리셋 시각 표시, 60초 내 갱신 |
| 5. 마감 | 카운트다운, 색상 전환, 설정 저장(위치·투명도·간격·행 표시), 자동 시작, 오류 상태 UI | 재부팅 후 자동 상주 동작 |
| 6. 배포 | `dotnet publish`(framework-dependent) → 시작 프로그램 등록 | 단독 exe 실행 확인 |

예상 작업량: 순수 구현 기준 반나절~1일.

★ 1단계가 핵심 리스크 해소 지점 — 여기서 응답 구조가 확정되어야 4단계 UI가 확정된다.

## 8. 리스크와 대응

| 리스크 | 대응 |
|---|---|
| 비공식 API·파일 포맷이 앱 업데이트로 변경될 수 있음 | 관대한 파싱, 소스 2중화, 실패 시 "확인 불가" 상태 표시 (앱이 죽지 않게) |
| 리프레시 토큰 회전 — write-back 실패 시 Claude 로그인이 깨질 수 있음 | 원자적 교체 + 백업, 쓰기 전 재읽기(경합 감지), 실패 시 갱신 중단 후 "claude 재로그인 필요" 안내 표시 |
| Fable 버킷이 계정/요금제에 따라 응답에 없을 수 있음 | 1단계에서 확인, 없으면 행 숨김 |
| npm CLI가 같은 credentials 파일을 동시에 갱신 | 파일 우선 원칙(위 3장) |
| DPI/다중 모니터 | per-monitor DPI aware 매니페스트, WorkArea 기준 배치 |

## 9. 전체 완료 기준

- [x] Claude 데스크톱 앱을 켜면 2~3초 내 사이드바 등장, 끄면 자동으로 사라짐 (프로세스 감지 동작 확인)
- [x] 5시간/주간/Fable/추가 사용량 4개 지표와 리셋 시각이 표시되고 60초 이내 주기로 갱신
- [x] 트레이 자동 상주 + HKCU Run 등록 (재부팅 실증은 다음 부팅 때 확인)
- [x] 토큰 만료 시 사용자 개입 없이 자동 복구 — 실증됨 (만료 토큰을 앱이 스스로 갱신·write-back)
- [x] Claude Code 로그인·동작에 부작용 없음 (write-back 후에도 정상 동작 확인)

## 10. 구현 결과 (2026-07-28)

- 실행 파일: `dist\ClaudeSidebar.exe` (framework-dependent, .NET 8 런타임 사용)
- 첫 실행 검증: 4개 지표 모두 API에서 정상 수신 확인
- **API 응답 실제 구조** (`docs/usage-api-schema.md` 참고):
  - `five_hour` / `seven_day`: `{utilization, resets_at}` — 예상대로
  - **Fable 주간 한도는 `limits[]` 배열의 `kind:"weekly_scoped"` 항목** (`scope.model.display_name:"Fable"`, `percent`) — `seven_day_opus` 등 구버전 필드는 전부 null
  - **크레딧은 `spend` 객체**: `used/limit.amount_minor`(센트 단위), `percent`, `enabled`, `disabled_reason`
  - 토큰 갱신 엔드포인트는 429 레이트리밋이 걸릴 수 있음 → 앱에 5분 백오프 내장
- 로그: `%APPDATA%\ClaudeSidebar\log.txt` / 설정: `%APPDATA%\ClaudeSidebar\settings.json`
- credentials 백업: `~/.claude/.credentials.json.bak` (갱신 직전 자동 생성)
- 트레이 "Claude 재로그인" 메뉴: cmd를 열어 `claude /login` 실행. 패널의 "재로그인 필요" 문구 클릭으로도 열림. 로그인 완료 후 다음 폴링(최대 60초)에서 자동 복구. 리프레시 토큰이 죽으면 갱신 시도를 10분 백오프

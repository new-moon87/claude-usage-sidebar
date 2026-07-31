# ClaudeSidebar 개발 명세서 — 원샷 구현용

> **사용법**: 이 문서 전체를 Claude(또는 다른 AI 코딩 도구)에 붙여넣고 "이 명세대로 구현해줘"라고 요청한다.
> 여기 적힌 내용은 전부 실제 구현·운영에서 **검증된 사실**이며, 시행착오 끝에 확정된 함정 회피책이 포함되어 있다.
> 문서와 다르게 구현하고 싶은 부분이 없다면 임의로 바꾸지 말 것 — 특히 "함정" 표시가 붙은 항목은 어기면 반드시 문제가 난다.

## 0. 한 줄 정의

Claude Code 사용량 한도 4종을 화면 오른쪽 가장자리에 폭 13px 알약으로 표시하는 Windows 상주 앱.
Claude Code 데스크톱 앱(`claude.exe`)이 실행 중일 때만 나타난다.

## 1. 확정 요구사항

### 표시 지표 (알약 4개, 위에서부터)

| 약자 | 지표 | 고유 색 |
|---|---|---|
| H | 5시간 세션 한도 | `#7F77DD` (보라) |
| W | 주간 한도 (전체 모델) | `#EF9F27` (주황) |
| F | 주간 한도 (모델 전용 — API가 주는 `display_name` 첫 글자로 동적 변경) | `#378ADD` (파랑) |
| C | 추가 사용량(Extra usage) 크레딧 | `#5DCAA5` (청록) |

- 사용률 85% 초과 시 해당 알약이 위험색 `#E24B4A`로 바뀌고 Opacity 1.0↔0.5 (700ms, AutoReverse, Forever) 펄스.
- 알약 안: 맨 위에 약자 1글자, 그 아래 % 숫자를 **한 자리씩 세로로 쌓아** 표시 (`string.Join("\n", v.ToString().ToCharArray())`).
- 알약은 세로 미니 게이지: 사용률만큼 아래에서 고유 색이 차오른다.

### 동작

- Claude 실행 감지 → 슬라이드바 페이드 인(180ms), 종료 감지 → 페이드 아웃(150ms).
- 알약에 마우스 올림 → 왼쪽으로 상세 패널 펼침. 벗어나면 350ms 후 접힘.
- 상세 패널: 지표명 · % · 게이지 바 · 리셋 시각 · 푸터(갱신 시각 HH:mm:ss + 새로고침 + 핀 버튼) · 오류 상태 줄.
- 핀 버튼(Segoe MDL2 Assets 글리프)으로 펼침 고정/해제. **알약 클릭으로는 고정하지 않는다** (드래그와 혼동됨 — 실사용 피드백으로 제거된 설계).
- 알약 드래그 → 위아래 위치 이동(저장). 가로는 항상 화면 오른쪽에 스냅.
- 트레이 아이콘 메뉴: 지금 새로고침 / Claude 재로그인(터미널 열기) / 항상 표시 / Windows 시작 시 실행 / 종료.

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
| H | 최상위 `five_hour: {utilization, resets_at}` |
| W | 최상위 `seven_day: {utilization, resets_at}` |
| F | **`limits[]` 배열에서 `kind == "weekly_scoped"`인 항목** → `percent`, `resets_at`, `scope.model.display_name`(예: "Fable") |
| C | `spend` 객체 → `percent`, `used.amount_minor`, `limit.amount_minor`, `exponent`(센트 단위), `enabled`, `disabled_reason`. 없으면 `extra_usage.utilization` 폴백 |

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

- 3초 간격 `Process.GetProcessesByName("claude")` — 결과의 각 `Process`를 반드시 `Dispose()`.
- 데스크톱 앱 엔진이 `claude.exe`라서 이걸로 충분하다. npm CLI는 node로 돌아 오탐 없음.
- 자기 자신은 `ClaudeSidebar.exe`로 명명해 충돌 회피.

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
   ├─ ProcessWatcher.cs          — 3.5
   ├─ SettingsStore.cs           — %APPDATA%\ClaudeSidebar\settings.json {Top, Pinned, Autostart, ForceShow}
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
> 재배치 연쇄). **창 크기를 232×320으로 고정**하고, 패널은 `Visibility.Hidden`(Collapsed 아님)으로 접는다 —
> 레이아웃 공간이 유지되어 창 크기가 안 변하고, AllowsTransparency 창에서 알파 0 영역은 클릭이 그대로 통과한다.

- 배치: `Left = SystemParameters.WorkArea.Right - Width`, Top은 저장값 또는 세로 중앙(WorkArea 기준으로 클램프).
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
- 타이머: 프로세스 감지 3초 / 사용량 폴링 60초(표시 중일 때만) / 카운트다운 재계산 30초.
- `SystemEvents.PowerModeChanged` Resume 시 즉시 갱신 (`Dispatcher.Invoke`로 감쌀 것).
- 시작 시퀀스: 설정 로드 → 폴백 파일로 첫 화면 즉시 표시 → 워처 시작 → 강제 갱신 1회.
- `RefreshAsync`: `_fetching` 가드(재진입 금지) → 429 백오프 중이면 즉시 반환 → 토큰 확보 → API → 실패 시 폴백 →
  `finally`에서 항상 `ApplySnapshot` + 로그 1줄 (`src=API H=.. W=.. F=.. C=.. status=..` — 이 로그가 검증 수단이다).
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

## 10. 알려진 한계

- 사용량·갱신 엔드포인트는 **비공식**이며 예고 없이 바뀔 수 있다. 파서가 관대해서 지표가 `-`로 빠질 뿐 앱은 죽지 않는다.
- 알약 사이 6px 틈은 히트테스트가 비어 있어, 정확히 그 지점에 커서를 두면 패널이 안 펼쳐진다 (실사용 영향 미미).
- 주 모니터 기준 배치 (다중 모니터에서 보조 모니터 지원은 미구현).
- 폴백 모드에서는 F 지표·리셋 시각이 없다 (3.4 참고).

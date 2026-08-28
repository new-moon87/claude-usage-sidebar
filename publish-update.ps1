<#
    새 버전을 빌드해 update/ 에 올리고 커밋·푸시한다.

    사용법:
        .\publish-update.ps1 -Version 0.1.1.0 -Notes "고친 것", "또 고친 것"

    호스팅은 이 저장소의 raw URL 을 그대로 쓴다 (Firebase·구글 자격 증명 불필요).
    version.json 은 raw.githubusercontent.com 이 몇 분 캐시하므로 즉시 반영되지는 않는다.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string[]]$Notes
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csproj = Join-Path $root "src\ClaudeSidebar\ClaudeSidebar.csproj"
$dist = Join-Path $root "dist"
$updateDir = Join-Path $root "update"
$manifest = Join-Path $updateDir "version.json"

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "버전은 x.y.z.w 형식이어야 합니다: $Version" }

# 1) csproj 버전을 올린다 — 클라이언트가 이 값으로 최신 여부를 판단한다.
(Get-Content $csproj -Raw) -replace '<Version>[\d.]+</Version>', "<Version>$Version</Version>" |
    Set-Content $csproj -Encoding utf8
Write-Host "csproj 버전 -> $Version"

# 2) 배포 빌드 (framework-dependent: 4파일, 압축 시 ~200KB)
Get-Process ClaudeSidebar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
& dotnet publish $csproj -c Release -o $dist --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

# 3) 압축 (pdb 는 뺀다)
New-Item -ItemType Directory -Force -Path $updateDir | Out-Null
Get-ChildItem $updateDir -Filter "ClaudeSidebar-*.zip" | Remove-Item -Force
$zip = Join-Path $updateDir "ClaudeSidebar-$Version.zip"
$files = Get-ChildItem $dist -File | Where-Object { $_.Extension -ne ".pdb" }
Compress-Archive -Path $files.FullName -DestinationPath $zip -Force
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "압축: $zip ($sizeMb MB) sha256=$sha"

# 4) 매니페스트 갱신 (history 앞에 추가)
$history = @()
if (Test-Path $manifest) {
    $old = Get-Content $manifest -Raw | ConvertFrom-Json
    if ($old.history) { $history = @($old.history) }
}
$entry = [ordered]@{ version = $Version; releasedAt = (Get-Date -Format "yyyy-MM-dd HH:mm"); notes = @($Notes) }
$history = @($entry) + $history

$repo = "new-moon87/claude-usage-sidebar"
[ordered]@{
    version     = $Version
    downloadUrl = "https://raw.githubusercontent.com/$repo/main/update/ClaudeSidebar-$Version.zip"
    sha256      = $sha
    notes       = @($Notes)
    releasedAt  = $entry.releasedAt
    sizeMb      = $sizeMb
    history     = $history
} | ConvertTo-Json -Depth 8 | Set-Content $manifest -Encoding utf8
Write-Host "매니페스트 갱신: $manifest"

# 배포 사이트(web/)도 같은 매니페스트를 본다. 같은 출처라 CORS 가 필요 없다.
Copy-Item $manifest (Join-Path $root "web/version.json") -Force

# 5) 커밋·푸시
& git -C $root add -A
& git -C $root commit -q -m "release $Version`n`n$($Notes -join "`n")"
& git -C $root push -q origin main
if ($LASTEXITCODE -ne 0) { throw "푸시 실패" }
Write-Host "배포 완료: $Version"

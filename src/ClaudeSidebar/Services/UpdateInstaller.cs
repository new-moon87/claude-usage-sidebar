using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace ClaudeSidebar;

// 새 버전을 내려받아 자기 자신을 갈아 끼운다.
//
// Windows 는 실행 중인 파일을 지우거나 덮어쓰지는 못해도 이름은 바꿀 수 있다. 그 성질로 자기 교체가 성립한다.
//   1. zip 을 받아 sha256 대조
//   2. 압축 안의 각 파일을 <이름>.new 로 푼다
//   3. 기존 파일을 <이름>.old 로 rename, .new 를 제자리로 rename
//   4. 새 exe 를 띄우고 자신은 종료. 다음 인스턴스가 .old 를 지운다
//
// index 메모판과 다른 점: 그쪽은 단일 exe 라 파일 1개만 바꾸면 됐다.
// 이쪽 배포본은 framework-dependent 라 exe·dll·deps.json·runtimeconfig.json 4개를 함께 바꿔야 한다.
// (exe 와 dll 은 실행 중 잠겨 있으므로 둘 다 rename 경로를 탄다.)
//
// 시작할 때만 적용한다. 쓰는 도중 재시작하면 사용자가 시킨 적 없는 일로 화면이 사라진다.
// 받은 파일은 믿지 않는다 — sha256 이 없거나 어긋나면 적용하지 않는다.
internal static class UpdateInstaller
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    public static string? CurrentExePath => Environment.ProcessPath;

    // 지난 업데이트가 남긴 .old 를 지운다. 실패해도 무시 — 백신이 잠깐 쥐고 있는 경우가 흔하다.
    public static void CleanupPreviousVersion()
    {
        if (CurrentExePath is not { } exe) return;
        try
        {
            foreach (var old in Directory.GetFiles(Path.GetDirectoryName(exe)!, "*.old"))
                TryDelete(old);
        }
        catch (Exception ex)
        {
            Log.Write($"[update] 이전 버전 정리 실패(무시): {ex.GetType().Name}");
        }
    }

    // 교체·재실행에 성공해 이 프로세스가 곧 종료돼야 하면 true.
    public static async Task<bool> ApplyAsync(UpdateCheckResult update, HttpClient http)
    {
        if (!update.IsUpdateAvailable
            || string.IsNullOrWhiteSpace(update.DownloadUrl)
            || CurrentExePath is not { } exe) return false;

        string dir = Path.GetDirectoryName(exe)!;

        // 자체 포함 단일 exe 배포본은 갈아 끼우지 않는다. 업데이트 zip 은 framework-dependent 4파일이라,
        // 런타임이 없는 PC 에서 바꿔 끼우면 앱이 아예 안 뜬다. (단일 파일에는 옆에 dll 이 없다.)
        if (!File.Exists(Path.Combine(dir, "ClaudeSidebar.dll")))
        {
            Log.Write("[update] 단일 파일 배포본 — 자동 교체 건너뜀");
            return false;
        }

        string archive = exe + ".zip";
        var staged = new List<(string Target, string New)>();
        var moved = new List<(string Target, string Old)>();

        try
        {
            if (!await DownloadAsync(update.DownloadUrl, archive, update.Sha256, http)) return false;

            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;   // 디렉터리 항목
                    string target = Path.Combine(dir, entry.Name);
                    string staging = target + ".new";
                    TryDelete(staging);
                    entry.ExtractToFile(staging, overwrite: true);
                    staged.Add((target, staging));
                }
            }

            if (!staged.Any(s => s.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                Log.Write("[update] 압축 안에 exe 가 없음 — 적용하지 않음");
                foreach (var s in staged) TryDelete(s.New);
                return false;
            }

            TryDelete(archive);

            // 여기서부터가 교체. 하나라도 실패하면 전부 되돌린다 — 반쯤 갈린 상태로 두면 앱이 아예 안 뜬다.
            foreach (var (target, staging) in staged)
            {
                if (File.Exists(target))
                {
                    string old = target + ".old";
                    TryDelete(old);
                    File.Move(target, old);
                    moved.Add((target, old));
                }
                File.Move(staging, target);
            }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Log.Write($"[update] 적용: {UpdateChecker.CurrentVersion} → {update.LatestVersion}. 재시작");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"[update] 적용 실패(이번 실행은 그대로 진행): {ex.GetType().Name} {ex.Message}");
            foreach (var (target, old) in moved)
            {
                try { TryDelete(target); File.Move(old, target); } catch { }
            }
            foreach (var s in staged) TryDelete(s.New);
            TryDelete(archive);
            return false;
        }
    }

    private static async Task<bool> DownloadAsync(string url, string target, string? expectedSha256, HttpClient http)
    {
        TryDelete(target);
        using (var cts = new CancellationTokenSource(DownloadTimeout))
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
        {
            resp.EnsureSuccessStatusCode();
            await using var file = File.Create(target);
            await resp.Content.CopyToAsync(file, cts.Token);
        }

        // 해시를 안 준 매니페스트는 믿지 않는다. 받은 것을 실행 파일 자리에 놓는 일이라,
        // 무결성을 확인할 방법이 없으면 아예 하지 않는 편이 낫다.
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            Log.Write("[update] 매니페스트에 sha256 없음 — 적용 건너뜀");
            TryDelete(target);
            return false;
        }

        string actual;
        using (var stream = File.OpenRead(target)) actual = Convert.ToHexString(SHA256.HashData(stream));

        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"[update] 해시 불일치 — 적용하지 않음. 기대 {expectedSha256}, 실제 {actual}");
            TryDelete(target);
            return false;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

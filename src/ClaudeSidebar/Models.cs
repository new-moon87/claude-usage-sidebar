namespace ClaudeSidebar;

public record UsageBucket(double? Utilization, DateTimeOffset? ResetsAt);

public class UsageSnapshot
{
    public UsageBucket? FiveHour;
    public UsageBucket? SevenDay;
    public UsageBucket? ModelWeekly;
    public string ModelWeeklyLabel = "";
    public double? ExtraUsagePct;
    public string? ExtraUsageDetail;
    public DateTimeOffset FetchedAt = DateTimeOffset.Now;
    public string Source = "API";
}

// Codex 한도 창 하나. 창 길이(초)를 반드시 같이 들고 다닌다 —
// 응답의 primary/secondary 라는 이름은 창 길이와 무관하다(함정 ⑯).
public record CodexWindow(double Percent, DateTimeOffset? ResetsAt, string Label, int WindowSeconds);

public class CodexSnapshot
{
    public CodexWindow? Short;        // 24시간 이하 창 중 사용률이 가장 높은 것
    public CodexWindow? Long;         // 24시간 초과 창 중 사용률이 가장 높은 것
    public string? CreditDetail;
    public string? PlanType;
    public DateTimeOffset FetchedAt = DateTimeOffset.Now;
    public string Source = "API";
}

public class AppSettings
{
    // 구버전 호환용(주 모니터 기준 DIP). 신규 저장은 MonitorName + PhysY 를 쓴다.
    public double? Top { get; set; }
    // 창을 붙여 둔 모니터의 장치 이름과 물리 픽셀 Y. 다중 모니터·혼합 DPI에서 유일하게 안전한 좌표다.
    public string? MonitorName { get; set; }
    public int? PhysY { get; set; }
    public bool Pinned { get; set; }
    public bool Autostart { get; set; } = true;
    public bool ForceShow { get; set; }
}

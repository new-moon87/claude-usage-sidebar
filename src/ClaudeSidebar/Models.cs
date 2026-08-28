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

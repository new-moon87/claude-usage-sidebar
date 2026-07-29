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
    public double? Top { get; set; }
    public bool Pinned { get; set; }
    public bool Autostart { get; set; } = true;
    public bool ForceShow { get; set; }
}

namespace NewHeights.TimeClock.Data.Entities;

public class TcPayRule
{
    public int PayRuleId { get; set; }
    public required string RuleName { get; set; }
    public string? Description { get; set; }
    public int RoundingIntervalMinutes { get; set; } = 15;
    public required string RoundingMethod { get; set; } = "NEAREST";
    public int GracePeriodMinutes { get; set; } = 5;
    public decimal OvertimeThresholdWeeklyHours { get; set; } = 40.00m;
    public decimal OvertimeMultiplier { get; set; } = 1.50m;
    public required string PayPeriodType { get; set; } = "BIWEEKLY";
    public int AutoDeductLunchMinutes { get; set; } = 0;
    public decimal RequireLunchPunchAfterHours { get; set; } = 6.00m;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<TcEmployee> Employees { get; set; } = new List<TcEmployee>();
}

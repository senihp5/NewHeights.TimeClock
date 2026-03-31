namespace NewHeights.TimeClock.Data.Entities;

public class TcBellPeriod
{
    public int PeriodId { get; set; }
    public int BellScheduleId { get; set; }
    public int PeriodNumber { get; set; }
    public string PeriodName { get; set; } = string.Empty;

    // CLASS, LUNCH, ADVISORY, BREAK
    public string PeriodType { get; set; } = "CLASS";

    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int TardyThresholdMinutes { get; set; } = 5;
    public int AbsentThresholdMinutes { get; set; } = 20;
    public bool IsActive { get; set; } = true;

    // Navigation
    public TcBellSchedule? Schedule { get; set; }
}

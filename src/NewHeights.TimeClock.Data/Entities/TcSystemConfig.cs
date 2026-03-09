namespace NewHeights.TimeClock.Data.Entities;

public class TcSystemConfig
{
    public string ConfigKey { get; set; } = string.Empty;
    public string ConfigValue { get; set; } = string.Empty;
    public string ConfigType { get; set; } = "STRING";
    public string? Description { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
}

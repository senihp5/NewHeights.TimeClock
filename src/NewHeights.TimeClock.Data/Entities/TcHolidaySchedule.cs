namespace NewHeights.TimeClock.Data.Entities;

public class TcHolidaySchedule
{
    public int HolidayId { get; set; }
    public string HolidayName { get; set; } = string.Empty;
    public DateOnly HolidayDate { get; set; }
    public decimal HoursCredited { get; set; } = 8.0m;
    public int? CampusId { get; set; }
    public bool AppliesToAllCampuses { get; set; } = true;
    public string SchoolYear { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string? CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    public Campus? Campus { get; set; }
}

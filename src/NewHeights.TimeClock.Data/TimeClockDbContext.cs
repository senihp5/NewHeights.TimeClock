using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data.Entities;

namespace NewHeights.TimeClock.Data;

public class TimeClockDbContext : DbContext
{
    public TimeClockDbContext(DbContextOptions<TimeClockDbContext> options)
        : base(options)
    {
    }

    public DbSet<Staff> Staff { get; set; } = null!;
    public DbSet<Student> Students { get; set; } = null!;
    public DbSet<Photo> Photos { get; set; } = null!;
    public DbSet<Campus> Campuses { get; set; } = null!;

    public DbSet<TcEmployee> TcEmployees { get; set; } = null!;
    public DbSet<TcPayRule> TcPayRules { get; set; } = null!;
    public DbSet<TcTimePunch> TcTimePunches { get; set; } = null!;
    public DbSet<TcDailyTimecard> TcDailyTimecards { get; set; } = null!;
    public DbSet<TcPayPeriod> TcPayPeriods { get; set; } = null!;
    public DbSet<TcPayPeriodSummary> TcPayPeriodSummaries { get; set; } = null!;
    public DbSet<TcCorrectionRequest> TcCorrectionRequests { get; set; } = null!;
    public DbSet<TcSubPool> TcSubPool { get; set; } = null!;
    public DbSet<TcSubRequest> TcSubRequests { get; set; } = null!;
    public DbSet<TcCalendarEvent> TcCalendarEvents { get; set; } = null!;
    public DbSet<TcPayrollExport> TcPayrollExports { get; set; } = null!;
    public DbSet<TcNotification> TcNotifications { get; set; } = null!;
    public DbSet<TcAuditLog> TcAuditLogs { get; set; } = null!;
    public DbSet<TcSystemConfig> TcSystemConfigs { get; set; } = null!;
    public DbSet<AttendanceTransaction> AttendanceTransactions { get; set; } = null!;

    // Added in migration 002
    public DbSet<TcPunchCorrection> TcPunchCorrections { get; set; } = null!;
    public DbSet<TcBellSchedule> TcBellSchedules { get; set; } = null!;
    public DbSet<TcBellPeriod> TcBellPeriods { get; set; } = null!;
    public DbSet<TcStaffHoursWindow> TcStaffHoursWindows { get; set; } = null!;
    public DbSet<TcMasterSchedule> TcMasterSchedules { get; set; } = null!;
    public DbSet<TcHolidaySchedule> TcHolidaySchedules { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureExistingTables(modelBuilder);
        ConfigureTimeclockTables(modelBuilder);
    }

    private static void ConfigureExistingTables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Staff>(entity =>
        {
            entity.ToTable("Staff");
            entity.HasKey(e => e.Dcid);
            entity.Property(e => e.IdNumber).HasMaxLength(50);
            entity.Property(e => e.FirstName).HasMaxLength(100);
            entity.Property(e => e.LastName).HasMaxLength(100);
            entity.Property(e => e.JobTitle).HasMaxLength(100);
            entity.Property(e => e.SchoolName).HasMaxLength(100);
            entity.Ignore(e => e.FullName);
        });

        modelBuilder.Entity<Student>(entity =>
        {
            entity.ToTable("Students");
            entity.HasKey(e => e.Dcid);
            entity.Property(e => e.StudentNumber).HasColumnName("IdNumber").HasMaxLength(50);
            entity.Property(e => e.FirstName).HasMaxLength(100);
            entity.Property(e => e.LastName).HasMaxLength(100);
            entity.Property(e => e.MiddleName).HasMaxLength(100);
            entity.Property(e => e.Grade).HasMaxLength(10);
            entity.Property(e => e.SchoolName).HasMaxLength(100);
            entity.Ignore(e => e.FullName);
        });

        modelBuilder.Entity<Photo>(entity =>
        {
            entity.ToTable("Photos");
            entity.HasKey(e => new { e.SubjectDcid, e.SubjectType });
        });

        modelBuilder.Entity<Campus>(entity =>
        {
            entity.ToTable("Attendance_Campuses");
            entity.HasKey(e => e.CampusId);
            entity.Property(e => e.CampusCode).HasMaxLength(20).IsRequired();
            entity.Property(e => e.CampusName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.SchoolNameValue).HasMaxLength(100);
            entity.Property(e => e.Latitude).HasColumnType("decimal(9,6)");
            entity.Property(e => e.Longitude).HasColumnType("decimal(9,6)");
            entity.Property(e => e.CampusWifiSSID).HasMaxLength(100);
        });
    }

    private static void ConfigureTimeclockTables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TcPayRule>(entity =>
        {
            entity.ToTable("TC_PayRules");
            entity.HasKey(e => e.PayRuleId);
            entity.Property(e => e.RuleName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(200);
            entity.Property(e => e.RoundingMethod).HasMaxLength(10).IsRequired();
            entity.Property(e => e.PayPeriodType).HasMaxLength(20).IsRequired();
            entity.Property(e => e.OvertimeThresholdWeeklyHours).HasColumnType("decimal(5,2)");
            entity.Property(e => e.OvertimeMultiplier).HasColumnType("decimal(3,2)");
            entity.Property(e => e.RequireLunchPunchAfterHours).HasColumnType("decimal(4,2)");
            entity.HasIndex(e => e.RuleName).IsUnique();
        });

        modelBuilder.Entity<TcEmployee>(entity =>
        {
            entity.ToTable("TC_Employees");
            entity.HasKey(e => e.EmployeeId);
            entity.Property(e => e.IdNumber).HasMaxLength(50).IsRequired();
            entity.Property(e => e.AscenderEmployeeId).HasMaxLength(50);
            entity.Property(e => e.EmployeeType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Shift).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.DepartmentCode).HasMaxLength(20);
            entity.Property(e => e.DepartmentName).HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(200);
            entity.Property(e => e.Phone).HasMaxLength(20);
            entity.Property(e => e.EntraObjectId).HasMaxLength(100);

            entity.HasOne(e => e.Staff).WithMany().HasForeignKey(e => e.StaffDcid).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            entity.HasOne(e => e.HomeCampus).WithMany(c => c.Employees).HasForeignKey(e => e.HomeCampusId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Supervisor).WithMany(s => s.DirectReports).HasForeignKey(e => e.SupervisorEmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.PayRule).WithMany(p => p.Employees).HasForeignKey(e => e.PayRuleId).OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.StaffDcid);
            entity.HasIndex(e => e.IdNumber);
            entity.HasIndex(e => e.EntraObjectId);
        });

        modelBuilder.Entity<TcTimePunch>(entity =>
        {
            entity.ToTable("TC_TimePunches");
            entity.HasKey(e => e.PunchId);
            entity.Property(e => e.PunchType).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.GeofenceStatus).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.PunchStatus).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.VerificationMethod).HasMaxLength(10).IsRequired();
            entity.Property(e => e.ScanMethod).HasMaxLength(10).IsRequired();
            entity.Property(e => e.QRCodeScanned).HasMaxLength(200);
            entity.Property(e => e.Latitude).HasColumnType("decimal(9,6)");
            entity.Property(e => e.Longitude).HasColumnType("decimal(9,6)");
            entity.Property(e => e.ModifiedBy).HasMaxLength(100);
            entity.Property(e => e.ModifiedReason).HasMaxLength(200);
            entity.Property(e => e.Notes).HasMaxLength(500);

            entity.HasOne(e => e.Employee).WithMany(emp => emp.TimePunches).HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Campus).WithMany(c => c.TimePunches).HasForeignKey(e => e.CampusId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.PairedPunch).WithMany().HasForeignKey(e => e.PairedPunchId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.PunchDateTime);
            entity.HasIndex(e => new { e.EmployeeId, e.PunchDateTime });
        });

        modelBuilder.Entity<TcDailyTimecard>(entity =>
        {
            entity.ToTable("TC_DailyTimecards");
            entity.HasKey(e => e.TimecardId);
            entity.Property(e => e.ApprovalStatus).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.RegularHours).HasColumnType("decimal(5,2)");
            entity.Property(e => e.OvertimeHours).HasColumnType("decimal(5,2)");
            entity.Property(e => e.TotalHours).HasColumnType("decimal(5,2)");
            entity.Property(e => e.ScheduledHours).HasColumnType("decimal(5,2)");
            
            entity.Property(e => e.ExceptionNotes).HasMaxLength(500);

            entity.HasOne(e => e.Employee).WithMany(emp => emp.DailyTimecards).HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Campus).WithMany().HasForeignKey(e => e.CampusId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.EmployeeId, e.WorkDate }).IsUnique();
            entity.HasIndex(e => e.WorkDate);
            entity.HasIndex(e => e.ApprovalStatus);
        });

        modelBuilder.Entity<TcPayPeriod>(entity =>
        {
            entity.ToTable("TC_PayPeriods");
            entity.HasKey(e => e.PayPeriodId);
            entity.Property(e => e.PeriodName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.LockedBy).HasMaxLength(100);
            entity.Property(e => e.SchoolYear).HasMaxLength(9);
            entity.Property(e => e.EmployeeDeadline);
            entity.Property(e => e.ExportedBy).HasMaxLength(100);
            entity.HasIndex(e => new { e.StartDate, e.EndDate });
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<TcPayPeriodSummary>(entity =>
        {
            entity.ToTable("TC_PayPeriodSummary");
            entity.HasKey(e => e.SummaryId);
            entity.Property(e => e.TotalRegularHours).HasColumnType("decimal(6,2)");
            entity.Property(e => e.TotalOvertimeHours).HasColumnType("decimal(6,2)");
            entity.Property(e => e.TotalHours).HasColumnType("decimal(6,2)");
            entity.Property(e => e.ApprovalStatus).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.SupervisorApprovedBy).HasMaxLength(100);
            entity.Property(e => e.HRApprovedBy).HasMaxLength(100);

            entity.HasOne(e => e.PayPeriod).WithMany(p => p.Summaries).HasForeignKey(e => e.PayPeriodId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Employee).WithMany(emp => emp.PayPeriodSummaries).HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.PayPeriodId, e.EmployeeId }).IsUnique();
        });

        modelBuilder.Entity<TcCorrectionRequest>(entity =>
        {
            entity.ToTable("TC_CorrectionRequests");
            entity.HasKey(e => e.RequestId);
            entity.Property(e => e.RequestType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.RequestedPunchType).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.Reason).HasMaxLength(500).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.ReviewedBy).HasMaxLength(100);
            entity.Property(e => e.ReviewNotes).HasMaxLength(500);

            entity.HasOne(e => e.Employee).WithMany(emp => emp.CorrectionRequests).HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.OriginalPunch).WithMany().HasForeignKey(e => e.OriginalPunchId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.AppliedPunch).WithMany().HasForeignKey(e => e.AppliedPunchId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.WorkDate);
        });

        modelBuilder.Entity<TcSubPool>(entity =>
        {
            entity.ToTable("TC_SubPool");
            entity.HasKey(e => e.SubPoolId);
            entity.Property(e => e.ExternalName).HasMaxLength(100);
            entity.Property(e => e.ExternalEmail).HasMaxLength(100);
            entity.Property(e => e.ExternalPhone).HasMaxLength(20);
            entity.Property(e => e.CertificationType).HasMaxLength(50);
            entity.Property(e => e.QualifiedSubjects).HasMaxLength(500);
            entity.Property(e => e.QualifiedGradeLevels).HasMaxLength(100);
            entity.Property(e => e.AvailableCampuses).HasMaxLength(50);
            entity.Property(e => e.Notes).HasMaxLength(500);

            entity.HasOne(e => e.Employee).WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TcSubRequest>(entity =>
        {
            entity.ToTable("TC_SubRequests");
            entity.HasKey(e => e.SubRequestId);
            entity.Property(e => e.AbsenceType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.AbsenceReason).HasMaxLength(500);
            entity.Property(e => e.PeriodsNeeded).HasMaxLength(50);
            entity.Property(e => e.SubjectArea).HasMaxLength(100);
            entity.Property(e => e.SpecialInstructions).HasMaxLength(1000);
            entity.Property(e => e.AssignedBy).HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.SupervisorApprovedBy).HasMaxLength(100);
            entity.Property(e => e.CalendarEventId).HasMaxLength(200);

            entity.HasOne(e => e.RequestingEmployee).WithMany(emp => emp.SubRequests).HasForeignKey(e => e.RequestingEmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Campus).WithMany().HasForeignKey(e => e.CampusId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.AssignedSub).WithMany(s => s.Assignments).HasForeignKey(e => e.AssignedSubPoolId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.StartDate, e.EndDate });
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<TcCalendarEvent>(entity =>
        {
            entity.ToTable("TC_CalendarEvents");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.EventType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Title).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.GraphEventId).HasMaxLength(200);
            entity.Property(e => e.SharedCalendarId).HasMaxLength(200);
            entity.Property(e => e.PersonalCalendarEventId).HasMaxLength(200);
            entity.Property(e => e.SyncStatus).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.SourceType).HasMaxLength(20);
            entity.Property(e => e.CreatedBy).HasMaxLength(100);

            entity.HasOne(e => e.Campus).WithMany().HasForeignKey(e => e.CampusId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Employee).WithMany(emp => emp.CalendarEvents).HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => new { e.StartDate, e.EndDate });
            entity.HasIndex(e => e.SyncStatus);
        });

        modelBuilder.Entity<TcPayrollExport>(entity =>
        {
            entity.ToTable("TC_PayrollExports");
            entity.HasKey(e => e.ExportId);
            entity.Property(e => e.ExportFormat).HasMaxLength(20);
            entity.Property(e => e.ExportMethod).HasMaxLength(20);
            entity.Property(e => e.FileName).HasMaxLength(200);
            entity.Property(e => e.TotalRegularHours).HasColumnType("decimal(8,2)");
            entity.Property(e => e.TotalOvertimeHours).HasColumnType("decimal(8,2)");
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(15);
            entity.Property(e => e.ExportedBy).HasMaxLength(100).IsRequired();

            entity.HasOne(e => e.PayPeriod).WithMany(p => p.Exports).HasForeignKey(e => e.PayPeriodId).OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.ExportedDate);
        });

        modelBuilder.Entity<TcNotification>(entity =>
        {
            entity.ToTable("TC_Notifications");
            entity.HasKey(e => e.NotificationId);
            entity.Property(e => e.NotificationType).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Channel).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.Subject).HasMaxLength(200);
            entity.Property(e => e.ReferenceType).HasMaxLength(30);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(10);
            entity.Property(e => e.ErrorMessage).HasMaxLength(500);

            entity.HasOne(e => e.RecipientEmployee).WithMany(emp => emp.Notifications).HasForeignKey(e => e.RecipientEmployeeId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.NotificationType);
        });
        modelBuilder.Entity<TcAuditLog>(entity =>
        {
            entity.ToTable("TC_AuditLog");
            entity.HasKey(e => e.AuditId);
            entity.Property(e => e.ActionCode).HasMaxLength(30).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(100).IsRequired();
            entity.Property(e => e.UserName).HasMaxLength(150).IsRequired();
            entity.Property(e => e.UserEmail).HasMaxLength(200);
            entity.Property(e => e.UserRole).HasMaxLength(30);
            entity.Property(e => e.EntityType).HasMaxLength(30).IsRequired();
            entity.Property(e => e.EntityId).HasMaxLength(50).IsRequired();
            entity.Property(e => e.DeltaSummary).HasMaxLength(500);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Source).HasMaxLength(15);
            entity.Property(e => e.IPAddress).HasMaxLength(50);
            entity.Property(e => e.SessionId).HasMaxLength(100);

            entity.HasIndex(e => e.ActionCode);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.EntityType, e.EntityId });
            entity.HasIndex(e => e.CreatedDate);
        });

        modelBuilder.Entity<TcSystemConfig>(entity =>
        {
            entity.ToTable("TC_SystemConfig");
            entity.HasKey(e => e.ConfigKey);
            entity.Property(e => e.ConfigKey).HasMaxLength(100);
            entity.Property(e => e.ConfigType).HasMaxLength(20);
            entity.Property(e => e.Description).HasMaxLength(200);
            entity.Property(e => e.ModifiedBy).HasMaxLength(100);
        });
        modelBuilder.Entity<AttendanceTransaction>(entity =>
        {
            entity.ToTable("Attendance_Transactions");
            entity.HasKey(e => e.TransactionId);
            entity.Property(e => e.TransactionType).HasMaxLength(20).IsRequired();
            entity.Property(e => e.IdNumber).HasMaxLength(50).IsRequired();
            entity.Property(e => e.FirstName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.LastName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ScanType).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ScanMethod).HasMaxLength(20).IsRequired();
            entity.Property(e => e.QRCodeScanned).HasMaxLength(200);
            entity.Property(e => e.DataSource).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ValidationStatus).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ValidationMessage).HasMaxLength(500);
            entity.Property(e => e.Notes).HasMaxLength(500);
            entity.HasOne(e => e.Campus).WithMany().HasForeignKey(e => e.CampusId);
            entity.HasIndex(e => new { e.IdNumber, e.ScanDateTime });
            entity.HasIndex(e => new { e.CampusId, e.ScanDateTime });
        });


        modelBuilder.Entity<TcPunchCorrection>(entity =>
        {
            entity.ToTable("TC_PunchCorrections");
            entity.HasKey(e => e.CorrectionId);
            entity.Property(e => e.CorrectionType).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(500);
            
            entity.HasOne(e => e.OriginalPunch).WithMany().HasForeignKey(e => e.OriginalPunchId).OnDelete(DeleteBehavior.Restrict);
            
        });

        modelBuilder.Entity<TcBellSchedule>(entity =>
        {
            entity.ToTable("TC_BellSchedule");
            entity.HasKey(e => e.BellScheduleId);
            entity.Property(e => e.ScheduleName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.SessionType).HasMaxLength(10).IsRequired();
            entity.HasOne(e => e.Campus).WithMany().HasForeignKey(e => e.CampusId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TcBellPeriod>(entity =>
        {
            entity.ToTable("TC_BellPeriod");
            entity.HasKey(e => e.PeriodId);
            entity.Property(e => e.PeriodName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.PeriodType).HasMaxLength(20).IsRequired();
            entity.HasOne(e => e.Schedule).WithMany(s => s.Periods).HasForeignKey(e => e.BellScheduleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TcStaffHoursWindow>(entity =>
        {
            entity.ToTable("TC_StaffHoursWindow");
            entity.HasKey(e => e.WindowId);
            entity.Property(e => e.SessionType).HasMaxLength(10).IsRequired();
            entity.HasOne(e => e.Campus).WithMany().HasForeignKey(e => e.CampusId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<TcMasterSchedule>(entity =>
        {
            entity.ToTable("TC_MasterSchedule");
            entity.HasKey(e => e.ScheduleId);
            entity.Property(e => e.RawPartnerNames).HasMaxLength(200);
            entity.Property(e => e.Room).HasMaxLength(10);
            entity.Property(e => e.DayPattern).HasMaxLength(10).IsRequired();
            entity.Property(e => e.Shift).HasMaxLength(10).IsRequired();
            entity.Property(e => e.ContentArea).HasMaxLength(20);
            entity.Property(e => e.MW_P1).HasMaxLength(100);
            entity.Property(e => e.MW_P2).HasMaxLength(100);
            entity.Property(e => e.MW_P3).HasMaxLength(100);
            entity.Property(e => e.MW_P4).HasMaxLength(100);
            entity.Property(e => e.MW_P5).HasMaxLength(100);
            entity.Property(e => e.MW_P6).HasMaxLength(100);
            entity.Property(e => e.TTh_P1).HasMaxLength(100);
            entity.Property(e => e.TTh_P2).HasMaxLength(100);
            entity.Property(e => e.TTh_P3).HasMaxLength(100);
            entity.Property(e => e.TTh_P4).HasMaxLength(100);
            entity.Property(e => e.TTh_P5).HasMaxLength(100);
            entity.Property(e => e.TTh_P6).HasMaxLength(100);
            entity.Property(e => e.TeacherMatchMethod).HasMaxLength(20);
            entity.Property(e => e.Partner1MatchMethod).HasMaxLength(20);
            entity.Property(e => e.Partner2MatchMethod).HasMaxLength(20);
            entity.Property(e => e.ImportedBy).HasMaxLength(150);
            entity.Property(e => e.Notes).HasMaxLength(500);

            entity.HasOne(e => e.Campus).WithMany().HasForeignKey(e => e.CampusId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Teacher).WithMany().HasForeignKey(e => e.TeacherStaffDcid).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            entity.HasOne(e => e.Partner1).WithMany().HasForeignKey(e => e.Partner1StaffDcid).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
            entity.HasOne(e => e.Partner2).WithMany().HasForeignKey(e => e.Partner2StaffDcid).OnDelete(DeleteBehavior.Restrict).IsRequired(false);

            entity.HasIndex(e => new { e.CampusId, e.TermName, e.SchoolYear });
            entity.HasIndex(e => e.TeacherStaffDcid);
        });

        modelBuilder.Entity<TcHolidaySchedule>(entity =>
        {
            entity.ToTable("TC_HolidaySchedule");
            entity.HasKey(e => e.HolidayId);
            entity.Property(e => e.HolidayName).HasMaxLength(100).IsRequired();
            entity.Property(e => e.HoursCredited).HasColumnType("decimal(5,2)");
            entity.Property(e => e.SchoolYear).HasMaxLength(9).IsRequired();
            entity.Property(e => e.CreatedBy).HasMaxLength(100);

            entity.HasOne(e => e.Campus).WithMany().HasForeignKey(e => e.CampusId).OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(e => e.HolidayDate);
            entity.HasIndex(e => e.SchoolYear);
        });
    }
}

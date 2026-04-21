namespace NewHeights.TimeClock.Shared.Constants;

public static class AppConstants
{
    public static class Roles
    {
        // Legacy flat roles (kept for backward compat)
        public const string Employee    = "TimeClock.Employee";
        public const string Supervisor  = "TimeClock.Supervisor";
        public const string Admin       = "TimeClock.Admin";

        // All salaried staff (parent group)
        public const string AllStaff    = "TimeClock.AllStaff";

        // Campus-scoped supervisors
        public const string SupervisorStopSix = "TimeClock.Supervisor.StopSix";
        public const string SupervisorMcCart  = "TimeClock.Supervisor.McCart";

        // Campus-scoped hourly employees (full-time, benefits-eligible)
        public const string EmployeeStopSix = "TimeClock.Employee.StopSix";
        public const string EmployeeMcCart  = "TimeClock.Employee.McCart";

        // Campus-scoped hourly employees (part-time, no holiday pay)
        public const string EmployeeStopSixPT = "TimeClock.Employee.StopSix.PT";
        public const string EmployeeMcCartPT  = "TimeClock.Employee.McCart.PT";

        // Substitutes (any campus, geofenced)
        public const string Substitute  = "TimeClock.Substitute";

        // HR — approved timesheets only
        public const string HR          = "TimeClock.HR";

        // Campus admins — attendance dashboards + reports
        public const string CampusAdmin = "TimeClock.CampusAdmin";

        // Reception staff - dashboard view + manual entry only (no admin functions)
        public const string Reception   = "TimeClock.Reception";

        // District staff - all-campus read-only view (no edit)
        public const string District    = "TimeClock.District";

        // Teachers — salaried classroom staff. Synced from Entra groups
        // GraphSync:TeacherGroupIds:StopSix / McCart (see EmployeeSyncService).
        // Teachers submit their own substitute requests but don't clock in for
        // payroll, so they're gated into RequireAnyStaff but not RequireHourly.
        public const string Teacher        = "TimeClock.Teacher";
        public const string TeacherStopSix = "TimeClock.Teacher.StopSix";
        public const string TeacherMcCart  = "TimeClock.Teacher.McCart";
    }

    public static class Campus
    {
        // Entra department values (primary campus identifier)
        public const string DeptStopSix  = "NHSS";
        public const string DeptMcCart   = "NHM";
        public const string DeptDistrict = "District";

        // Entra officeLocation values (display / fallback)
        public const string OfficeLabelStopSix  = "New Heights - Stop Six";
        public const string OfficeLabelMcCart   = "New Heights - McCart";
        public const string OfficeLabelDistrict = "District Office";

        // Internal campus codes used in DB and geofence. These MUST match the
        // Attendance_Campuses.CampusCode values on disk. The Stop Six row has
        // stored "STOPSIX" since the initial schema; the previous "STOP6"
        // constant caused silent lookup failures across EmployeeSync and
        // holiday seeding. Normalized 2026-04-20.
        public const string StopSixCode  = "STOPSIX";
        public const string McCartCode   = "MCCART";
        public const string DistrictCode = "DISTRICT";

        // PowerSchool campus IDs (used in Attendance_Transactions)
        public const int StopSixPowerSchoolId  = 220822001;
        public const int McCartPowerSchoolId   = 220822002;
    }

    public static class Defaults
    {
        public const int     GeofenceRadiusMeters    = 150;
        public const int     RoundingIntervalMinutes = 15;
        public const int     GracePeriodMinutes      = 5;
        public const decimal OvertimeThresholdHours  = 40.00m;
        public const decimal OvertimeMultiplier      = 1.50m;
    }

    public static class Branding
    {
        public const string NavyColor  = "#1e3a5f";
        public const string GoldColor  = "#f5b81c";
        public const string SchoolName = "New Heights High School";
        public const string ShortName  = "New Heights";
        public const string Mascot     = "Phoenix";
        public const string Website    = "https://newheightseducation.com";
    }
}

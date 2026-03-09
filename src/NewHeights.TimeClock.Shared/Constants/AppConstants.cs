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

        // Campus-scoped hourly employees
        public const string EmployeeStopSix = "TimeClock.Employee.StopSix";
        public const string EmployeeMcCart  = "TimeClock.Employee.McCart";

        // Substitutes (any campus, geofenced)
        public const string Substitute  = "TimeClock.Substitute";

        // HR — approved timesheets only
        public const string HR          = "TimeClock.HR";

        // Campus admins — attendance dashboards + reports
        public const string CampusAdmin = "TimeClock.CampusAdmin";
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

        // Internal campus codes used in DB and geofence
        public const string StopSixCode  = "STOP6";
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
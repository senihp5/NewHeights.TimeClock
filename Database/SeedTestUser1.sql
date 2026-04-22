-- ═══════════════════════════════════════════════════════════════════════
-- Test employee seed: TestUser1 — McCart full-time hourly
-- ═══════════════════════════════════════════════════════════════════════
-- Not a migration. One-off seed script for a test account. Run via SSMS
-- against the TimeClock database.
--
-- Why not sync?
--   EmployeeSyncService skips any non-substitute Entra user that doesn't
--   match a Staff row in the IDSuite3 PowerSchool database. Test accounts
--   don't exist in PowerSchool, so hourly test users get logged as
--   "No Staff record found for X — skipping" and never land in
--   TC_Employees. Subs bypass this check (they're allowed to exist with
--   StaffDcid NULL and an auto-generated SUB-NNNN IdNumber).
--
-- Before running:
--   1. Replace @EntraObjectId with TestUser1's actual Object ID from the
--      Azure portal (Entra ID > Users > TestUser1 > Object ID field).
--   2. Confirm @Email matches the preferred_username / UPN they sign in
--      with. Typically "testuser1@newheightsed.com" but double-check.
--   3. Make sure TestUser1 is also assigned one of the Employee app roles
--      on the TimeClock Enterprise Application — either individually or
--      via membership in a McCart hourly group. Without the role claim,
--      RequireHourly policy will block /my/timesheet regardless of the
--      TC_Employees row. See our earlier conversation on Azure app-role
--      assignment for Jasmine/Isaac.
--
-- Idempotent: safe to re-run. Updates in place if a row already exists
-- with the matching EntraObjectId; otherwise inserts fresh.

SET NOCOUNT ON;

DECLARE @EntraObjectId NVARCHAR(100) = 'PASTE-TESTUSER1-ENTRA-OBJECT-ID-HERE';
DECLARE @Email         NVARCHAR(200) = 'testuser1@newheightsed.com';
DECLARE @DisplayName   NVARCHAR(200) = 'Test User One';
DECLARE @IdNumber      NVARCHAR(50)  = 'TEST-EMP1';
DECLARE @CampusCode    NVARCHAR(20)  = 'MCCART';
DECLARE @DeptCode      NVARCHAR(10)  = 'NHM';

-- Enum values (see Shared/Enums):
--   EmployeeType.HourlyStaff    = 2
--   EmployeeType.HourlyPartTime = 3
--   EmployeeShift.Day           = 0
DECLARE @EmployeeType  INT = 2;
DECLARE @Shift         INT = 0;

-- Resolve campus id from code so the script works across environments
-- (prod / staging / dev might have different Campuses.CampusId values).
DECLARE @HomeCampusId INT;
SELECT @HomeCampusId = CampusId FROM Campuses WHERE CampusCode = @CampusCode;

IF @HomeCampusId IS NULL
BEGIN
    RAISERROR('Campus %s not found in Campuses table. Aborting.', 16, 1, @CampusCode);
    RETURN;
END

IF @EntraObjectId = 'PASTE-TESTUSER1-ENTRA-OBJECT-ID-HERE'
BEGIN
    RAISERROR('Replace @EntraObjectId with the actual Entra Object ID before running.', 16, 1);
    RETURN;
END

IF EXISTS (SELECT 1 FROM TC_Employees WHERE EntraObjectId = @EntraObjectId)
BEGIN
    UPDATE TC_Employees
       SET DisplayName    = @DisplayName,
           Email          = @Email,
           IdNumber       = @IdNumber,
           EmployeeType   = @EmployeeType,
           Shift          = @Shift,
           HomeCampusId   = @HomeCampusId,
           DepartmentCode = @DeptCode,
           IsActive       = 1,
           ModifiedDate   = GETDATE()
     WHERE EntraObjectId = @EntraObjectId;

    PRINT 'Updated existing TC_Employees row for TestUser1 (EntraObjectId match).';
END
ELSE IF EXISTS (SELECT 1 FROM TC_Employees WHERE IdNumber = @IdNumber)
BEGIN
    -- IdNumber collision: likely a previous run without EntraObjectId. Update
    -- the existing row to bring it in sync with the new Entra identity.
    UPDATE TC_Employees
       SET EntraObjectId  = @EntraObjectId,
           DisplayName    = @DisplayName,
           Email          = @Email,
           EmployeeType   = @EmployeeType,
           Shift          = @Shift,
           HomeCampusId   = @HomeCampusId,
           DepartmentCode = @DeptCode,
           IsActive       = 1,
           ModifiedDate   = GETDATE()
     WHERE IdNumber = @IdNumber;

    PRINT 'Updated existing TC_Employees row for TestUser1 (IdNumber match, linked EntraObjectId).';
END
ELSE
BEGIN
    INSERT INTO TC_Employees (
        StaffDcid,
        IdNumber,
        DisplayName,
        AscenderEmployeeId,
        EmployeeType,
        Shift,
        HomeCampusId,
        SupervisorEmployeeId,
        DepartmentCode,
        Email,
        IsActive,
        EntraObjectId,
        SmsOptedOut,
        ExcludedFromSubPool,
        CreatedDate,
        ModifiedDate
    )
    VALUES (
        NULL,                -- StaffDcid: no PowerSchool link
        @IdNumber,
        @DisplayName,
        NULL,                -- AscenderEmployeeId: no payroll number for test
        @EmployeeType,
        @Shift,
        @HomeCampusId,
        NULL,                -- SupervisorEmployeeId: left null; sync will
                             --   link when a supervisor is identified via
                             --   Entra manager attribute (if configured).
        @DeptCode,
        @Email,
        1,                   -- IsActive
        @EntraObjectId,
        0,                   -- SmsOptedOut
        0,                   -- ExcludedFromSubPool
        GETDATE(),
        GETDATE()
    );

    PRINT 'Created TestUser1 in TC_Employees.';
END

-- Confirmation query — run this on its own to verify.
SELECT EmployeeId, IdNumber, DisplayName, Email, EmployeeType, HomeCampusId, IsActive, EntraObjectId
  FROM TC_Employees
 WHERE EntraObjectId = @EntraObjectId;

GO

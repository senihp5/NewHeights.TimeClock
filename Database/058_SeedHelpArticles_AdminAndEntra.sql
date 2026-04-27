/*
===============================================================================
Migration 058 — Seed help articles: Admin + Entra Groups
Date: 2026-04-27
Purpose:
  Pass 2 part 3 of the help-article seed. Admin-only articles covering every
  admin tool in the portal, plus a dedicated Entra Groups section since
  group-membership confusion is the #1 source of "I can''t see X" tickets.

  Idempotent. Same MERGE-with-ModifiedBy-guard pattern as 055/056/057.

Sections in this batch:
  - Admin            (section_order = 8, RequireAdmin, 9 articles)
  - Entra Groups     (section_order = 9, RequireAdmin, 3 articles)
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 058: Seed Pass 2 part 3 (Admin + Entra Groups)';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

DECLARE @now DATETIME = GETDATE();

;WITH src AS (
    SELECT * FROM (VALUES

    -- ── Admin (RequireAdmin) ─────────────────────────────────────────────────

    (N'employee-sync', N'admin', N'Admin', 8, 1, N'RequireAdmin',
     N'Managing employees (sync + manual add)',
     N'Pull from PowerSchool, or add a special-case employee by hand.',
     N'<p>Two paths exist for getting an employee into <code>TC_Employees</code> — automatic sync from PowerSchool, or a manual one-off add.</p>
<h3>PowerSchool sync</h3>
<p>Open <strong>Admin</strong> &rarr; <strong>Employees</strong> (<code>/admin/employees/sync</code>). The page shows the most recent sync run and a button to trigger a new one. Sync pulls every active staff record from PowerSchool, matches by <code>StaffDcid</code>, and creates / updates <code>TC_Employees</code> rows accordingly.</p>
<h3>Manual add (special cases)</h3>
<p>Use <strong>Admin</strong> &rarr; <strong>Add Employee (Manual)</strong> for substitutes, contractors, or test accounts that don''t exist in PowerSchool.</p>
<ul>
  <li><strong>EmployeeId</strong> auto-generates — leave blank.</li>
  <li><strong>EntraObjectId</strong> is the user''s Azure AD object ID (find in Entra admin center).</li>
  <li><strong>IdNumber</strong> is the badge number — must be unique. By convention substitutes use the 9000-9999 range.</li>
  <li><strong>SchoolId / CampusId</strong> — assign to the right campus. Multi-campus subs need a row per campus.</li>
</ul>
<h3>Reactivating an inactive employee</h3>
<p>If an employee was marked inactive but came back, find them via <strong>Admin</strong> &rarr; <strong>Audit Log</strong> filter on EmployeeId, then run an UPDATE in SSMS: <code>UPDATE TC_Employees SET IsActive=1 WHERE EmployeeId={id}</code>. The portal picks up the flip on the next request.</p>'),

    (N'pay-periods', N'admin', N'Admin', 8, 2, N'RequireAdmin',
     N'Pay periods configuration',
     N'Define the semi-monthly windows the timesheet rolls over.',
     N'<p>Pay periods are the date windows that group daily timecards into payroll batches. Configure them at <code>/admin/pay-periods</code>.</p>
<h3>The fields</h3>
<ul>
  <li><strong>PeriodName</strong> — display label, e.g. "Apr 1&ndash;15, 2026".</li>
  <li><strong>StartDate / EndDate</strong> — inclusive on both ends.</li>
  <li><strong>EmployeeDeadline</strong> — when employees must submit by (default = EndDate + 2).</li>
  <li><strong>SupervisorDeadline</strong> — when supervisors must approve by (default = EndDate + 3).</li>
  <li><strong>HRDeadline</strong> — when HR must approve by (default = EndDate + 4 = payroll cut day).</li>
  <li><strong>IsOpen</strong> — whether the period is the currently-active period for new submissions.</li>
</ul>
<h3>Conventions</h3>
<p>NH runs <strong>semi-monthly</strong>: 1&ndash;15 and 16&ndash;EOM. Always create both halves of the month at the same time so the dropdown on /my/timesheet shows the upcoming period and not just the current one.</p>
<h3>Closing a period after export</h3>
<p>Setting <code>IsOpen=0</code> on an exported period prevents employees from re-opening it accidentally. The export action does this automatically — manual flip should rarely be needed.</p>'),

    (N'holiday-bell', N'admin', N'Admin', 8, 3, N'RequireAdmin',
     N'Holiday + Bell schedules',
     N'Configure paid holidays and the period-by-period bell schedule per campus.',
     N'<p>These two admin pages drive how the portal calculates hours and which periods exist in a session.</p>
<h3>Holiday Schedule (<code>/admin/holidays</code>)</h3>
<p>Add one row per paid school holiday for the current school year. Required: Date, HolidayName, HoursCredited (typically 8). The hourly timesheet auto-credits the hours to all active hourly employees on that date.</p>
<p>Don''t forget district-level holidays the school is closed for that aren''t obvious from the school calendar (Memorial Day, ML King, etc.). The Holiday seed migration covers the standard set; mid-year additions go through this page.</p>
<h3>Bell Schedules (<code>/admin/bell-schedules</code>)</h3>
<p>Each campus has at least one Day session and one Night session bell schedule. Each schedule has a list of <strong>periods</strong> with start/end times and a period type (CLASS / LUNCH / BREAK).</p>
<ul>
  <li><strong>Stop Six</strong> Day = P1&ndash;P4, Night = P5&ndash;P6.</li>
  <li><strong>McCart</strong> matches Stop Six''s period numbers but with its own bell times.</li>
</ul>
<h3>Editing a schedule</h3>
<p>You can mark periods inactive (<code>IsActive=0</code>) instead of deleting them — preserves history. The <strong>IsDefault</strong> flag controls which schedule the SubRequest period picker pulls from when more than one matching schedule exists.</p>'),

    (N'schedule-import', N'admin', N'Admin', 8, 4, N'RequireAdmin',
     N'Schedule Import',
     N'Pull the master class schedule from PowerSchool so the sub picker auto-populates teachers.',
     N'<p>The Substitute period picker auto-fills teacher / content area / room number from the master schedule. That data lives in <code>TC_MasterSchedule</code> and gets refreshed via <strong>Admin</strong> &rarr; <strong>Schedule Import</strong>.</p>
<h3>When to run it</h3>
<ul>
  <li>At the start of each new term (TERM1 through TERM4).</li>
  <li>Mid-term if a teacher swap or room change happens and the picker shows the wrong teacher.</li>
  <li>One-off when a multi-campus teacher is missing from one campus''s picker.</li>
</ul>
<h3>How it works</h3>
<p>The page asks for SchoolId (Stop Six = 1, McCart = 2), TermName (e.g. <code>TERM3</code>), and SchoolYear (e.g. <code>2025-26</code>). It then queries PowerSchool for every section taught that term + school + year, joins to staff and rooms, and upserts to <code>TC_MasterSchedule</code>.</p>
<h3>Multi-campus teachers</h3>
<p>Teachers who teach at both campuses have <em>two</em> Staff DCIDs (one per SchoolId). The import scopes by campus so cross-campus matches don''t pollute each other''s pickers.</p>
<h3>What if numbers seem off after import</h3>
<p>Check the <strong>Audit Log</strong> for the <code>SCHEDULE_IMPORT_RAN</code> entry — it includes counts of inserted / updated / skipped rows per campus. If skipped is high, the most likely cause is staff DCID mismatches that need a one-off SQL UPDATE.</p>'),

    (N'sub-pool-mgmt', N'admin', N'Admin', 8, 5, N'RequireAdmin',
     N'Sub Pool Management',
     N'Add subs, set their specialties, mark them active or retired.',
     N'<p>The sub pool is the list of people the cascade considers when a teacher submits a sub request. Manage it at <code>/admin/sub-pool-management</code>.</p>
<h3>Adding a new sub</h3>
<ol>
  <li>Make sure the sub has an Entra account and is in <code>TimeClock.Substitute</code>.</li>
  <li>Make sure they have a <code>TC_Employees</code> row (manual add if not in PowerSchool).</li>
  <li>On Sub Pool Management, click <strong>Add to Pool</strong> and pick the sub from the dropdown.</li>
  <li>Set their default campus and assign specialty tags.</li>
</ol>
<h3>Specialties</h3>
<p>Specialties are content-area tags (Math, ELA, Science, History, ESL, Special Ed, etc.). The cascade prioritizes subs whose specialty matches the requested teacher''s content area before opening to general subs.</p>
<h3>Inactive vs. removed</h3>
<p>Mark a sub <strong>Inactive</strong> when they''re on long-term leave or retired. They''re hidden from the cascade but their TC_Employees row stays for audit. Don''t hard-delete sub rows — historical timecards reference them via foreign key.</p>'),

    (N'audit-log', N'admin', N'Admin', 8, 6, N'RequireAdmin',
     N'Reading the Audit Log',
     N'Where to look when something needs explaining.',
     N'<p>The audit log (<code>/admin/audit</code>) records every state-changing action across the portal. If something happened, it''s in here.</p>
<h3>Filters</h3>
<ul>
  <li><strong>ActionCode</strong> — exact match. Common codes: <code>SUB_AUTO_APPROVED</code>, <code>SUB_TAKEN_OVER</code>, <code>TIMESHEET_REMINDER_SENT</code>, <code>SCHEDULE_IMPORT_RAN</code>, <code>EMPLOYEE_REACTIVATED</code>.</li>
  <li><strong>EntityType / EntityId</strong> — narrow to a specific record.</li>
  <li><strong>Source</strong> — User (interactive click), System (background service), Service (cross-service call).</li>
  <li><strong>EmployeeId</strong> — who did it (or who it was done to).</li>
  <li><strong>Date range</strong> — defaults to the last 30 days.</li>
</ul>
<h3>What to read first</h3>
<p>The <strong>DeltaSummary</strong> column has a one-line plain-English description of the change. The <strong>NewValues</strong> JSON has the full payload. <strong>OldValues</strong> is populated on UPDATE-style actions only.</p>
<h3>SQL for power users</h3>
<p>The page is fine for browsing but cumbersome for trend analysis. For "how many take-overs happened in March?" run a query against <code>TC_AuditLog</code> directly — the table is indexed on <code>(ActionCode, CreatedDate)</code>.</p>'),

    (N'hourly-csv-import', N'admin', N'Admin', 8, 7, N'RequireAdmin',
     N'Hourly CSV Import',
     N'Bulk-load timecard hours from a Google Form spreadsheet.',
     N'<p>For employees who fill out paper / Google Form timesheets instead of clocking the kiosk, the CSV import (<code>/admin/hourly-csv-import</code>) backfills <code>TC_DailyTimecards</code> and <code>TC_TimePunches</code> from a CSV.</p>
<h3>Supported formats</h3>
<p>Four format variants are observed in the wild — Jasmine''s, Karina''s, Griselda''s, Leighla''s. The importer auto-detects which format by header signature. New variants need a code update; talk to Patrick before adding a fifth.</p>
<h3>Steps</h3>
<ol>
  <li>Download the Google Form responses as CSV.</li>
  <li>Upload to <code>/admin/hourly-csv-import</code>.</li>
  <li>The page parses, matches each row to a <code>TC_Employees</code> record, and shows a preview.</li>
  <li>Review the preview — flagged rows (no employee match, invalid time, etc.) need manual edits in the source spreadsheet.</li>
  <li>Click <strong>Import</strong>. Each row creates timepunches + a daily timecard.</li>
</ol>
<h3>Idempotency</h3>
<p>Re-importing the same file is safe — the importer keys on (EmployeeId, WorkDate) and skips rows that already have punches.</p>'),

    (N'sub-paper-entry', N'admin', N'Admin', 8, 8, N'RequireAdmin',
     N'Sub Paper Entry',
     N'Manually enter sub coverage from a paper form.',
     N'<p>If a substitute couldn''t use the portal (no phone, kiosk down, special-case fill-in), use <code>/admin/sub-paper-entry</code> to enter their coverage by hand.</p>
<h3>What you''ll need</h3>
<ul>
  <li>The paper sub timecard signed by the campus manager.</li>
  <li>The substitute''s name (the page''s search will resolve to a TcEmployees row).</li>
  <li>The campus, the date, and each period covered with the teacher being covered.</li>
</ul>
<h3>Steps</h3>
<ol>
  <li>Pick the substitute from the search.</li>
  <li>Pick the date and campus.</li>
  <li>For each period covered, select the period and the teacher (same picker as on /my/sub-timesheet).</li>
  <li>Save. The system creates a <code>TC_SubstituteTimecard</code> + <code>TC_SubstitutePeriodEntries</code> exactly as if the sub had logged on the portal.</li>
</ol>
<h3>Approval</h3>
<p>Paper-entered timecards still need campus manager approval like any other. The save action sets ApprovalStatus = Pending; the campus manager approves on their normal Sub Timesheet review page.</p>'),

    (N'admin-utilities', N'admin', N'Admin', 8, 9, N'RequireAdmin',
     N'Timecard Recalc + Email Test',
     N'Two utility pages for fixing data and verifying email config.',
     N'<h3>Timecard Recalc (<code>/admin/timecard-recalc</code>)</h3>
<p>Re-runs the daily-timecard rollup logic for a date range. Use when:</p>
<ul>
  <li>Historical punches were edited and the rolled-up daily totals on Team Timesheets / HR Payroll are stale.</li>
  <li>A bell schedule or pay rule changed mid-period and you need to re-apply it retroactively.</li>
  <li>An import or correction batch left timecards inconsistent.</li>
</ul>
<p>The recalc is idempotent — running it twice gives the same result. Run scoped (one employee, narrow date range) when possible, then expand if needed.</p>
<h3>Email Test (<code>/admin/email-test</code>)</h3>
<p>Sends a no-op test email through the configured SMTP relay. Useful when:</p>
<ul>
  <li>Setting up a new App Service slot and verifying SMTP creds load.</li>
  <li>Diagnosing "did the cascade email actually send" issues — the test email uses the same EmailService.SendEmailAsync code path so success here means cascade emails work too.</li>
  <li>Checking that bounces are being handled (check the SMTP relay logs after sending).</li>
</ul>
<p>The test email body includes the timestamp and the App Service instance ID for easy correlation with logs.</p>'),

    -- ── Entra Groups (RequireAdmin) ──────────────────────────────────────────

    (N'entra-groups-overview', N'entra-groups', N'Entra Groups', 9, 1, N'RequireAdmin',
     N'How Entra groups gate the portal',
     N'Sign-in groups → policy memberships → page visibility.',
     N'<p>The portal authorizes every page and feature based on the user''s <strong>Entra security group memberships</strong>. There are no per-user permissions stored locally — Entra is the source of truth. Add or remove a user from a group in Entra and the next sign-in reflects the change.</p>
<h3>The flow</h3>
<ol>
  <li>User signs in via Microsoft Identity (OIDC).</li>
  <li>The sign-in callback reads the user''s group memberships from the ID token claims.</li>
  <li>Group GUIDs are mapped to friendly role names (<code>TimeClock.Supervisor</code>, etc.) via the GroupRoleMapping config in <code>appsettings.json</code>.</li>
  <li>Each AuthorizeView policy (<code>RequireSupervisor</code>, etc.) checks for the friendly role name. The same role can satisfy multiple policies (e.g. Supervisor satisfies Reception too).</li>
</ol>
<h3>Refresh after group change</h3>
<p>Group changes take effect on the user''s <strong>next sign-in</strong>, not the next request. After adding a user to a group, tell them to sign out (top-right) and sign back in. The token is otherwise cached for the session.</p>
<h3>Where the mapping lives</h3>
<p>App Service Configuration: settings prefixed with <code>GroupRoleMapping__</code>. One entry per friendly role mapping its GUID. Don''t edit these in source control — they live in App Service config so dev/staging/prod can target different Entra tenants.</p>'),

    (N'timeclock-group-reference', N'entra-groups', N'Entra Groups', 9, 2, N'RequireAdmin',
     N'TimeClock.* group reference',
     N'Every group, what it unlocks, and who should be in it.',
     N'<p>Reference for each TimeClock.* security group. <strong>Add membership in the Entra admin center, not the portal.</strong></p>
<h3>Staff-tier groups</h3>
<table style="width:100%; border-collapse: collapse; font-size: 0.92rem;">
<thead><tr style="background:#f3f4f6;"><th style="text-align:left; padding:0.4rem;">Group</th><th style="text-align:left; padding:0.4rem;">Who</th><th style="text-align:left; padding:0.4rem;">Unlocks</th></tr></thead>
<tbody>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.AllStaff</code></td><td style="padding:0.4rem;">Every active staff member</td><td style="padding:0.4rem;">Sign-in, home page, /help, sub request submission</td></tr>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.Employee.PartTime</code></td><td style="padding:0.4rem;">Hourly part-time employees</td><td style="padding:0.4rem;">/my/timesheet, /mobile/checkin punches, correction requests</td></tr>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.Substitute</code></td><td style="padding:0.4rem;">Substitute teachers</td><td style="padding:0.4rem;">/my/sub-timesheet, sub respond, /sub/my-assignments</td></tr>
</tbody>
</table>
<h3>Supervisor / specialty-role groups</h3>
<table style="width:100%; border-collapse: collapse; font-size: 0.92rem;">
<thead><tr style="background:#f3f4f6;"><th style="text-align:left; padding:0.4rem;">Group</th><th style="text-align:left; padding:0.4rem;">Who</th><th style="text-align:left; padding:0.4rem;">Unlocks</th></tr></thead>
<tbody>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.Supervisor</code></td><td style="padding:0.4rem;">Direct supervisors at any campus</td><td style="padding:0.4rem;">Team Timesheets, Sub Calendar, Sub Requests, sub-timesheet review</td></tr>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.Supervisor.StopSix</code></td><td style="padding:0.4rem;">Stop Six campus supervisors</td><td style="padding:0.4rem;">Same as above, scoped to Stop Six employees</td></tr>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.Supervisor.McCart</code></td><td style="padding:0.4rem;">McCart campus supervisors</td><td style="padding:0.4rem;">Same as above, scoped to McCart employees</td></tr>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.HR</code></td><td style="padding:0.4rem;">HR staff</td><td style="padding:0.4rem;">Payroll Review, payroll CSV export, employee record edits</td></tr>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.Reception</code></td><td style="padding:0.4rem;">Front-desk receptionists</td><td style="padding:0.4rem;">Reception live dashboard, Safety report, kiosk navigation</td></tr>
<tr style="border-top:1px solid #e5e7eb;"><td style="padding:0.4rem;"><code>TimeClock.Admin</code></td><td style="padding:0.4rem;">IT / system administrators</td><td style="padding:0.4rem;">Every page in the portal, "Show all roles" help toggle, audit log</td></tr>
</tbody>
</table>
<h3>The TimeClock.Principals group (pending)</h3>
<p>Reserved Entra group <code>29458d93-94fa-49cf-986f-0f507a4e5932</code>. Not yet wired to a policy as of 2026-04-27. Three options on the table — see the Permissions Matrix handoff for which one ships.</p>
<h3>Convention</h3>
<p>Group names are namespaced under <code>TimeClock.</code> to keep them visually separate from the school''s other Entra groups (Staff, AllStudents, etc.). Don''t rename existing groups; the GUID-to-friendly-name mapping is what the code reads.</p>'),

    (N'entra-troubleshooting', N'entra-groups', N'Entra Groups', 9, 3, N'RequireAdmin',
     N'Common Entra group issues',
     N'Why a user can''t see X — and the fastest path to fixing it.',
     N'<h3>"User says they can''t see Team Timesheets"</h3>
<ol>
  <li>Confirm in Entra: are they in <code>TimeClock.Supervisor</code> AND the campus-specific group (<code>TimeClock.Supervisor.StopSix</code> or <code>.McCart</code>)? Both are required for the campus-scoped views.</li>
  <li>If yes, did they sign out and sign back in <em>after</em> the group was added? Group membership is cached in the OIDC token until next sign-in.</li>
  <li>If still no, check the audit log for <code>USER_SIGNED_IN</code> entries for that user — the entry includes the resolved roles. If TimeClock.Supervisor isn''t in the list, the GroupRoleMapping config in App Service is missing or stale.</li>
</ol>
<h3>"User has dual roles, only seeing one menu"</h3>
<p>Dual roles work — a user in both Supervisor and HR sees both menus. If they''re seeing only one, look at policy precedence. Some pages use <code>RequireSupervisor</code> only; some use <code>RequireAnyStaff</code> + their own role check inside the page. Confirm the user is in <em>all</em> the friendly roles you expect by hovering their initial in the top-right corner.</p>
<h3>"New employee added to Entra group but portal still shows ''no employee record''"</h3>
<p>Two-step process. Entra group adds them to the portal''s authorization layer, but they also need a row in <code>TC_Employees</code>. Run the PowerSchool sync (<code>/admin/employees/sync</code>) — that creates the row. Or use Manual Add for someone outside PowerSchool.</p>
<h3>"Substitute can''t see /my/sub-timesheet"</h3>
<p>Substitutes need both <code>TimeClock.Substitute</code> AND <code>TimeClock.Employee.PartTime</code>. The Substitute group alone authorizes the sub-respond + assignments pages. PartTime is what authorizes the timesheet pages. Subs that only have one of the two see a partial menu.</p>
<h3>"Removed user from a group, they still see the page"</h3>
<p>Group removal also takes effect on next sign-in. To force-revoke immediately, disable the user''s Entra account or reset their MFA — that invalidates active tokens.</p>
<h3>Where to confirm the GroupRoleMapping</h3>
<p>App Service &rarr; Configuration &rarr; Application settings. Look for entries starting <code>GroupRoleMapping__</code>. Each entry maps a friendly role name to an Entra group GUID. Adding a new group requires both an Entra group create AND an App Service config add — restart the service for the new mapping to load.</p>')

    ) v(Slug, SectionKey, SectionTitle, SectionOrder, ArticleOrder, PolicyName, Title, Summary, BodyHtml)
)
MERGE TC_HelpArticles AS tgt
USING src
   ON tgt.Slug = src.Slug
WHEN MATCHED AND tgt.ModifiedBy IS NULL THEN
    UPDATE SET
        Title         = src.Title,
        SectionKey    = src.SectionKey,
        SectionTitle  = src.SectionTitle,
        SectionOrder  = src.SectionOrder,
        ArticleOrder  = src.ArticleOrder,
        PolicyName    = src.PolicyName,
        Summary       = src.Summary,
        BodyHtml      = src.BodyHtml,
        IsActive      = 1,
        ModifiedDate  = @now
WHEN NOT MATCHED THEN
    INSERT (Slug, Title, SectionKey, SectionTitle, SectionOrder, ArticleOrder, PolicyName, Summary, BodyHtml, IsActive, CreatedDate, ModifiedDate, ModifiedBy)
    VALUES (src.Slug, src.Title, src.SectionKey, src.SectionTitle, src.SectionOrder, src.ArticleOrder, src.PolicyName, src.Summary, src.BodyHtml, 1, @now, @now, NULL);

PRINT '    Seeded Pass 2 part 3 (Admin + Entra Groups).';
GO

PRINT 'Migration 058 complete.';
GO

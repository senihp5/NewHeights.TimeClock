/*
===============================================================================
Migration 057 — Seed help articles: Supervisor + Reception + HR & Payroll
Date: 2026-04-27
Purpose:
  Pass 2 part 2 of the help-article seed. Covers mid-tier roles —
  supervisors approving timesheets and sub requests, reception running the
  live dashboards, and HR closing out payroll.

  Idempotent. Same MERGE-with-ModifiedBy-guard pattern as 055/056.

Sections in this batch:
  - Supervisor       (section_order = 5, RequireSupervisor, 5 articles)
  - Reception        (section_order = 6, RequireReception, 2 articles)
  - HR & Payroll     (section_order = 7, RequireHR, 4 articles)
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 057: Seed Pass 2 part 2 (Supervisor / Reception / HR)';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

DECLARE @now DATETIME = GETDATE();

;WITH src AS (
    SELECT * FROM (VALUES

    -- ── Supervisor (RequireSupervisor) ───────────────────────────────────────

    (N'approving-timesheets', N'supervisor', N'Supervisor', 5, 1, N'RequireSupervisor',
     N'Approving your team''s timesheets',
     N'How to review, approve, and clear exceptions on Team Timesheets.',
     N'<p>Supervisor approval is stage 2 of the three-stage payroll flow (employee submit &rarr; supervisor approve &rarr; HR approve). You handle this on <code>/supervisor/timesheets</code>.</p>
<h3>The page layout</h3>
<p>One row per direct report, plus campus and supervisor columns if you''re an admin viewing all teams. Pick a pay period from the dropdown at the top.</p>
<ul>
  <li><strong>Emp. Status</strong> — has the employee submitted? "Submitted" = ready for you. "Pending" = still working on it.</li>
  <li><strong>Sup. Status</strong> — your action state. "Ready" = waiting for your approval. "Approved" = done. "Waiting" = employee hasn''t submitted yet.</li>
  <li><strong>Exceptions</strong> — yellow chip with a count. Click to jump straight to the exceptions section of the single-employee review page.</li>
  <li><strong>Flags</strong> — Short-Day Reasons claimed by the employee (PTO, Sick, etc.). One chip per reason.</li>
</ul>
<h3>To approve</h3>
<ol>
  <li>Click <strong>View</strong> on a "Ready" row.</li>
  <li>Review the daily totals, especially the days flagged as exceptions.</li>
  <li>Resolve any leftover exceptions — accept the Short-Day Reason, approve a punch correction, or reject the timesheet back to the employee for fix.</li>
  <li>Click <strong>Approve Timesheet</strong> at the bottom.</li>
</ol>
<h3>If the employee hasn''t submitted</h3>
<p>Click <strong>Remind</strong> on the row. The system sends a templated email (and SMS if they''re opted in) and logs the reminder so HR can see it was sent.</p>'),

    (N'sub-calendar', N'supervisor', N'Supervisor', 5, 2, N'RequireSupervisor',
     N'Reviewing the Sub Calendar',
     N'Weekly grid view of every sub assignment for your campus.',
     N'<p>The Sub Calendar (<code>/supervisor/sub-calendar</code>) shows a week at a glance — Mon&ndash;Fri across the top, periods down the side, colored cells for every sub assignment.</p>
<h3>Color legend</h3>
<ul>
  <li><strong>Green (Approved)</strong> — supervisor-approved request. All set.</li>
  <li><strong>Yellow (Needs approval)</strong> — sub has accepted; waiting for you to approve.</li>
  <li><strong>Orange (Awaiting Sub acceptance)</strong> — request out, no acceptances yet.</li>
</ul>
<h3>Day vs Night</h3>
<p>The grid splits into two tables — Day session (P1&ndash;P4) on top, Night session (P5&ndash;P6) below. Each table only shows the periods that exist in that session''s bell schedule.</p>
<h3>Multi-teacher cells</h3>
<p>When two or more teachers need a sub for the same period on the same day, the cell stacks per-teacher lines. Each line is independently clickable AND independently colored — so you can see at a glance that one line is yellow (Needs approval) while another is orange (still searching).</p>
<h3>Week navigation</h3>
<p>Use the <strong>Week</strong> dropdown at the top to jump 8 weeks back or 4 weeks forward. The "Today" button only appears when you''re viewing a non-current week.</p>
<h3>Click a cell</h3>
<p>Tap any colored cell to open the detail panel below the grid. From there you can <a href="#take-over-request">Take Over</a> the request if it''s stuck.</p>'),

    (N'take-over-request', N'supervisor', N'Supervisor', 5, 3, N'RequireSupervisor',
     N'Taking over a sub request manually',
     N'Day-of override: cancel outreach, flip Emergency, manually assign a sub.',
     N'<p>The Take-Over feature is for the day-of-absence cases where the cascade hasn''t landed coverage. You skip the queue and assign a sub directly.</p>
<h3>When to use it</h3>
<ul>
  <li>It''s the morning of the absence and no sub has accepted.</li>
  <li>You know a specific sub is available and want to put them on the request.</li>
  <li>Coverage is partial (some periods covered) and you need to manually assign the remainder.</li>
</ul>
<h3>Steps</h3>
<ol>
  <li>Open the Sub Calendar and click the cell for the request.</li>
  <li>In the detail panel, click <strong>Take Over Request</strong>.</li>
  <li>The system cancels any in-flight outreach for that request and flips the request to <em>Emergency</em> for audit.</li>
  <li>Pick the substitute from the dropdown. The list is every active sub at your campus — not just the original cascade pool.</li>
  <li>Tick the period checkboxes for what this sub will cover.</li>
  <li>Click <strong>Assign</strong>. The sub gets a confirmation email and SMS.</li>
</ol>
<h3>What gets logged</h3>
<p>Three audit codes appear in <code>TC_AuditLog</code> for the take-over: <code>SUB_TAKEN_OVER</code> (the click), <code>SUB_MANUAL_ASSIGN</code> (each manual sub-period assignment), and any subsequent <code>SUB_AUTO_APPROVED</code> when the request hits its absence date with all periods covered. HR uses these for end-of-period audit.</p>'),

    (N'sending-reminders', N'supervisor', N'Supervisor', 5, 4, N'RequireSupervisor',
     N'Sending a timesheet reminder',
     N'Nudge a direct report whose timesheet is sitting in "Pending".',
     N'<p>From <strong>Team Timesheets</strong>, employees whose status is "Pending" (not submitted yet) show a <strong>Remind</strong> button instead of <strong>Approve</strong>.</p>
<h3>How it works</h3>
<ol>
  <li>Click <strong>Remind</strong> on the employee''s row.</li>
  <li>The system sends a pre-templated email reminder telling them the pay period deadline and a direct link to <code>/my/timesheet</code>.</li>
  <li>SMS goes too if the employee has a phone on file and isn''t opted out.</li>
  <li>An inline status banner shows whether the send succeeded or failed, and the action is logged with codes <code>TIMESHEET_REMINDER_SENT</code> / <code>_FAILED</code> in <code>TC_AuditLog</code>.</li>
</ol>
<h3>Dedup safeguard</h3>
<p>The portal also runs an automated reminder service. Both the manual and the automated send go through the same <code>TC_TimesheetReminderLog</code> table so a duplicate within 6 hours is suppressed — your reminder won''t double-fire if you click within hours of the auto job.</p>
<h3>If reminders aren''t working</h3>
<p>Check the inline status banner first. Common failures: the employee has no email on file (talk to HR / Patrick), or SMS opt-out for everyone is enabled in config (admin only).</p>'),

    (N'exceptions-chip', N'supervisor', N'Supervisor', 5, 5, N'RequireSupervisor',
     N'Reading the Exceptions chip on Team Timesheets',
     N'Yellow ⚠ chip on a row — click for fast deep-link to the day.',
     N'<p>The <strong>Exceptions</strong> column shows a yellow chip with a count for any employee whose timesheet has unresolved exceptions in the active pay period.</p>
<h3>What counts as an exception</h3>
<p>Same definitions as on the employee''s view: missed punches, hours under shift schedule with no Short-Day Reason, punches outside the shift window, manual entries flagged for review.</p>
<h3>What clicking does</h3>
<p>Tapping the chip jumps you to the single-employee timesheet review page, scrolled directly to the <code>#exceptions</code> anchor. You can resolve each exception in place — accept the reason, approve a correction, or reject and add a note.</p>
<h3>Once exceptions are resolved</h3>
<p>The chip disappears from the team roster the next time the page loads. The row''s "Sup. Status" should flip to "Ready" once the employee has submitted and you''ve cleared the exceptions.</p>'),

    -- ── Reception (RequireReception) ─────────────────────────────────────────

    (N'live-dashboard', N'reception', N'Reception', 6, 1, N'RequireReception',
     N'Reception live dashboard',
     N'Real-time view of who''s on site at each campus.',
     N'<p>The Reception dashboard (<code>/reception</code>) shows campus presence in real time — staff and student counts, recent scan activity, and quick links to the kiosk and Safety Dashboard.</p>
<h3>What''s on the page</h3>
<ul>
  <li><strong>Campus tabs</strong> at top — switch between Stop Six and McCart. Your last selection is remembered in browser storage.</li>
  <li><strong>Stat pills</strong> — Staff On Site, Students On Site, Total, and Scans Today. The total is computed from the latest IN/OUT scan per person.</li>
  <li><strong>Recent activity feed</strong> — the last 15 scans, color-coded by type (campus in, campus out, lunch out, lunch in).</li>
  <li><strong>Quick action cards</strong> — direct links to the Reception Kiosk, Safety Dashboard, and per-campus kiosk URLs.</li>
</ul>
<h3>How fresh is the data</h3>
<p>The dashboard auto-refreshes every 15 seconds. The "Updated" stamp at the top right shows when the last refresh ran. Click the 🔄 button for an immediate refresh.</p>
<h3>Who else sees this</h3>
<p>The same dashboard appears as the top section on the home page for any role with the Reception policy (Reception, Supervisor variants, Admin). Same data, same auto-refresh.</p>'),

    (N'safety-report', N'reception', N'Reception', 6, 2, N'RequireReception',
     N'Safety dashboard',
     N'Muster + accountability view for emergency scenarios.',
     N'<p>The Safety Dashboard (<code>/safety</code>) is the muster view — used for fire drills, lockdowns, and any situation where you need to confirm who''s actually present.</p>
<h3>Key differences vs the live dashboard</h3>
<ul>
  <li>Shows people <strong>by name</strong>, not just counts. You can scroll the list and check off the people you can physically account for.</li>
  <li>Sortable by name, role (Staff vs Student), and last scan time.</li>
  <li>The list is the authoritative roster of who the system thinks is on site at this moment based on the most recent IN scan that hasn''t been followed by an OUT.</li>
</ul>
<h3>Drill into a person</h3>
<p>Tap any name to see their last scan time, type (campus in / lunch in / etc.), and how long they''ve been on site. Useful for verifying a borderline case.</p>
<h3>Print-friendly</h3>
<p>The page is designed to print cleanly — landscape, big names, scan times. Useful as a paper backup if the network is down during a drill.</p>'),

    -- ── HR & Payroll (RequireHR) ─────────────────────────────────────────────

    (N'payroll-cycle', N'hr-payroll', N'HR &amp; Payroll', 7, 1, N'RequireHR',
     N'Payroll cycle overview',
     N'The four stages from employee submit to Ascender export.',
     N'<p>The portal handles four explicit stages of the payroll cycle. HR plays the gatekeeper at stages 3 and 4.</p>
<h3>The four stages</h3>
<ol>
  <li><strong>Employee submit (E)</strong> — the hourly employee reviews their timesheet and clicks Submit. Locks edits.</li>
  <li><strong>Supervisor approve (S)</strong> — direct supervisor approves on Team Timesheets. They can return the timesheet to the employee with a note if there''s an issue.</li>
  <li><strong>HR approve (HR)</strong> — HR reviews on Payroll Review (<code>/hr/payroll</code>) and approves. This is the final approval before export.</li>
  <li><strong>Export</strong> — HR downloads the CSV for Ascender import. Once exported, the pay period locks completely.</li>
</ol>
<h3>Status indicators</h3>
<p>The Payroll Review page shows three checkmark slots per row: <em>E&#10003; / E&#9203;</em>, <em>S&#10003; / S&#9203;</em>, <em>HR&#10003; / HR&#9203;</em>. The hourglass means the stage is still pending.</p>
<h3>Deadlines</h3>
<p>Each stage has its own deadline, staggered so HR doesn''t do everything in one panic-day. By default: employee deadline = payroll-cut minus 2 days; supervisor deadline = payroll-cut minus 1 day; HR deadline = payroll-cut day.</p>'),

    (N'hr-approving', N'hr-payroll', N'HR &amp; Payroll', 7, 2, N'RequireHR',
     N'HR-approving timesheets',
     N'Final approval pass on Payroll Review.',
     N'<p>HR approval is stage 3. By the time a timesheet reaches you, the employee has submitted and the supervisor has approved. You''re the last set of eyes before export.</p>
<h3>The page</h3>
<p>Open <code>/hr/payroll</code>. Pick the active pay period from the dropdown. The table shows one row per employee scoped to the period.</p>
<h3>Filter by status</h3>
<p>Use the status filter dropdown to narrow to "Ready for HR" (supervisor-approved, not HR-approved yet). That''s usually what you want.</p>
<h3>Approving</h3>
<ul>
  <li><strong>Approve All Ready</strong> — bulk-approves every row whose status is supervisor-approved and has no exceptions. Use this once you''ve hand-reviewed exceptions separately.</li>
  <li><strong>Approve</strong> on individual rows — approves one at a time. Useful if you want to handle some rows separately from the bulk.</li>
</ul>
<h3>Returning a timesheet</h3>
<p>If you spot a problem during HR review, click <strong>View</strong> to open the single-employee page and reject from there. The rejection re-opens the timesheet for the employee with your note attached. The employee re-submits, supervisor re-approves, and it lands back in your queue.</p>'),

    (N'payroll-csv-export', N'hr-payroll', N'HR &amp; Payroll', 7, 3, N'RequireHR',
     N'Exporting payroll CSV',
     N'Generate the file Ascender imports.',
     N'<p>Once every employee in the pay period is HR-approved, the <strong>Export CSV</strong> button on Payroll Review enables.</p>
<h3>Export prereq</h3>
<p>The button is disabled until <em>all</em> employees in the period are HR-approved. If even one row is short on approval, the button stays grayed. Hover the button for a tooltip listing what''s missing.</p>
<h3>What''s in the file</h3>
<p>Standard Ascender layout: EmployeeId, Name, Period dates, RegularHours, OvertimeHours, Earnings codes, Department codes. Format matches the import template Ascender supplies.</p>
<h3>After export</h3>
<ul>
  <li>The pay period is locked. No more edits, approvals, or rejections.</li>
  <li>The page shows "Exported on [date] by [your email]" so the audit trail is visible to anyone who reopens the period.</li>
  <li>If you discover a mistake post-export, talk to Patrick. The pay period unlock is admin-only and audited.</li>
</ul>
<h3>Where the file goes</h3>
<p>The CSV downloads through your browser. Save it to wherever your Ascender import process lives. Don''t email the file unencrypted — it contains payroll data.</p>'),

    (N'reviewing-exceptions', N'hr-payroll', N'HR &amp; Payroll', 7, 4, N'RequireHR',
     N'Reviewing exceptions before HR approval',
     N'Catch the unresolved cases before bulk-approve runs.',
     N'<p>The HR Payroll page surfaces an <strong>Exceptions</strong> chip per row, identical in semantics to the supervisor version. Click any chip to jump to the day-level detail.</p>
<h3>Why HR cares</h3>
<p>Supervisors usually clear exceptions before approving. But edge cases land in HR''s lap:</p>
<ul>
  <li><strong>Manual entries the supervisor accepted but didn''t justify</strong> — needs a note.</li>
  <li><strong>Out-of-window punches over a custom threshold</strong> — automatically retained but worth a glance.</li>
  <li><strong>Overtime hours not pre-approved</strong> — exceptions show OT > 0 with no flag noting prior auth.</li>
</ul>
<h3>Quick triage</h3>
<ol>
  <li>Sort the table by Exceptions descending so the noisiest rows are at the top.</li>
  <li>Click each yellow chip in turn — each opens the day in the single-employee view at the exceptions anchor.</li>
  <li>Either approve, or reject the timesheet back to the supervisor (rare; usually a phone call is faster).</li>
</ol>
<h3>Bulk-approve safety</h3>
<p>The <strong>Approve All Ready</strong> button skips any row with an unresolved exception. So even if you forget to triage, exceptions don''t slip into the export by accident.</p>')

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

PRINT '    Seeded Pass 2 part 2 (Supervisor + Reception + HR & Payroll).';
GO

PRINT 'Migration 057 complete.';
GO

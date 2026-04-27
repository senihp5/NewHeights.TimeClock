/*
===============================================================================
Migration 056 — Seed help articles: My Time + Substitute Work + Sub Requests
Date: 2026-04-27
Purpose:
  Pass 2 part 1 of the help-article seed. Covers the day-to-day workflows for
  hourly employees, substitutes, and teachers requesting subs.

  Idempotent — same MERGE pattern as 055; rows where ModifiedBy IS NOT NULL
  are protected from re-seed overwrites so admin edits in production survive.

Sections in this batch:
  - My Time          (section_order = 2, RequireHourly, 5 articles)
  - Substitute Work  (section_order = 3, RequireHourly, 4 articles)
  - Sub Requests     (section_order = 4, RequireAnyStaff, 4 articles)
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 056: Seed Pass 2 part 1 (Hourly + Teachers)';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

DECLARE @now DATETIME = GETDATE();

;WITH src AS (
    SELECT * FROM (VALUES

    -- ── My Time (RequireHourly) ──────────────────────────────────────────────

    (N'reading-your-timesheet', N'my-time', N'My Time', 2, 1, N'RequireHourly',
     N'Reading your timesheet on /my/timesheet',
     N'Pay-period view: how rows, columns, and the colored status pills work.',
     N'<p>Open <code>/my/timesheet</code> from the <strong>My Time</strong> menu, or tap the <strong>My Timesheet</strong> card on the home page.</p>
<h3>What you''re looking at</h3>
<p>The page shows one row per workday in the current pay period. Pick a different period from the <strong>Pay Period</strong> dropdown at the top.</p>
<ul>
  <li><strong>Date</strong> — day name + date. The first time you punched in and the last time you punched out are shown right under the date.</li>
  <li><strong>AM / PM / Evening</strong> — colored status pills showing whether you were clocked in for each shift window. Green = on time, yellow = something to review.</li>
  <li><strong>Worked / Leave / Holiday</strong> — hours by category for that day.</li>
  <li><strong>Total</strong> — the day total. Green if it matches your scheduled hours, yellow if short.</li>
</ul>
<h3>Drilling into a day</h3>
<p>Tap any day row to expand it. You''ll see your individual punch pairs (in → out), the rounded times used for payroll, and the source (badge scan vs. manual entry vs. modified). On mobile this opens as a full-screen sheet.</p>
<h3>The bottom row</h3>
<p>The blue <strong>Pay Period Totals</strong> row at the bottom adds everything up. Overtime, if any, is shown in amber under the total.</p>'),

    (N'submitting-timesheet', N'my-time', N'My Time', 2, 2, N'RequireHourly',
     N'Submitting your timesheet for the pay period',
     N'Lock your hours and route to your supervisor for approval.',
     N'<p>Once you''ve checked your hours and any exceptions are resolved, submit your timesheet so it routes to your supervisor.</p>
<h3>Steps</h3>
<ol>
  <li>Open <code>/my/timesheet</code> for the pay period you''re submitting.</li>
  <li>Review every day in the period. Look for yellow <strong>has-exception</strong> rows — those need a Short-Day Reason or a punch correction before submission.</li>
  <li>Scroll to the bottom and click <strong>Submit Timesheet</strong>.</li>
  <li>The page will lock — you can no longer add corrections after submitting.</li>
</ol>
<h3>Deadlines</h3>
<p>The submission deadline shows in the info bar at the top. It''s usually <strong>2 days before payroll cuts</strong>. Days remaining show beside the date; if there are 2 or fewer the bar turns red.</p>
<h3>If you miss the deadline</h3>
<p>The system will email a reminder once. If you still don''t submit, your supervisor can submit on your behalf. Talk to them as soon as you notice — last-minute submissions pile risk on payroll.</p>
<h3>I submitted, now what?</h3>
<p>Your supervisor reviews and approves on their <strong>Team Timesheets</strong> page. After supervisor approval, HR reviews and approves on <strong>Payroll Review</strong>. You''ll see the status update on your timesheet header.</p>'),

    (N'requesting-correction', N'my-time', N'My Time', 2, 3, N'RequireHourly',
     N'Requesting a punch correction',
     N'Fix a missed clock-in / clock-out without waiting for payroll.',
     N'<p>If you forgot to clock in or out, or your time was wrong, request a correction <em>before</em> you submit your timesheet. Corrections submitted after submission still work but require your supervisor to unlock the timesheet.</p>
<h3>How to request</h3>
<ol>
  <li>Open <code>/my/timesheet</code> and find the day with the issue.</li>
  <li>Tap the day row to expand the punch detail.</li>
  <li>Click <strong>📝 Request Correction</strong>.</li>
  <li>Pick the type — <em>Missed Punch</em>, <em>Wrong Time</em>, <em>Forgot to Clock Out</em>, etc.</li>
  <li>Fill in what should have happened (time + brief reason).</li>
  <li>Submit. Your supervisor sees a notification and approves or denies.</li>
</ol>
<h3>What happens after</h3>
<p>Approved corrections automatically rewrite the punches on your timesheet. Denied corrections stay on record for the audit trail; you''ll get an email explaining why.</p>
<h3>If your supervisor isn''t responsive</h3>
<p>Tell HR. Persistent un-actioned corrections will block payroll for that pay period.</p>'),

    (N'timesheet-exceptions', N'my-time', N'My Time', 2, 4, N'RequireHourly',
     N'Why does my timesheet show an exception?',
     N'Common reasons rows turn yellow and how to clear them.',
     N'<p>An <strong>exception</strong> is the system''s way of flagging a day that doesn''t look right. The row is shaded yellow and the day total may be missing or off.</p>
<h3>The most common causes</h3>
<ul>
  <li><strong>Missed punch</strong> — you clocked in but never clocked out (or vice versa). The row shows "(still in)" if you forgot to clock out.</li>
  <li><strong>Hours under your scheduled day</strong> — you worked less than your assigned shift window. May need a Short-Day Reason (PTO, Sick, Weather Closure, etc.).</li>
  <li><strong>Punch outside your shift window</strong> — clocked in 30 min before your window opens, or out 30 min after it closes. Your supervisor decides whether to keep or trim.</li>
  <li><strong>Manual entry</strong> — punches you typed yourself instead of badge-scanning are flagged for visibility, not as an error.</li>
</ul>
<h3>How to clear it</h3>
<p>Most exceptions are cleared either by adding a Short-Day Reason on the day''s detail panel, or by submitting a Punch Correction. Once you submit the timesheet, your supervisor reviews any remaining exceptions during approval.</p>'),

    (N'short-day-reason', N'my-time', N'My Time', 2, 5, N'RequireHourly',
     N'Using the Short-Day Reason field',
     N'When you worked less than scheduled, tell payroll why.',
     N'<p>If a day shows fewer hours than your scheduled shift, the system flags it. Adding a <strong>Short-Day Reason</strong> tells payroll why so it stops looking like an error.</p>
<h3>Where to find it</h3>
<ol>
  <li>Open <code>/my/timesheet</code> and tap the short day to expand it.</li>
  <li>Inside the punch detail panel, look for the <strong>Reason for short / non-work day</strong> dropdown.</li>
  <li>Pick the reason. Add an optional note for context (e.g. "Dr appointment 1:30").</li>
</ol>
<h3>Available reasons</h3>
<ul>
  <li><strong>Weather Closure</strong> — campus was closed (snow, storm).</li>
  <li><strong>PTO</strong> — paid time off used.</li>
  <li><strong>Sick</strong> — sick leave used.</li>
  <li><strong>Personal</strong> — personal leave.</li>
  <li><strong>Holiday</strong> — paid holiday hours.</li>
  <li><strong>Professional Dev</strong> — training / PD day off-site.</li>
  <li><strong>Other</strong> — anything else; note required.</li>
</ul>
<h3>Why it matters</h3>
<p>Reasons feed payroll''s exception report. Days with a reason set don''t show as "missing hours" to your supervisor or HR. A day with hours under schedule and <em>no</em> reason will block timesheet approval until either a correction is filed or a reason is added.</p>'),

    -- ── Substitute Work (RequireHourly, sub-tagged) ──────────────────────────

    (N'logging-sub-periods', N'substitute-work', N'Substitute Work', 3, 1, N'RequireHourly',
     N'Logging substitute periods on /my/sub-timesheet',
     N'Track each class period you covered as a substitute.',
     N'<p>Substitutes don''t use the regular hourly timesheet — they track work by <strong>period</strong> on <code>/my/sub-timesheet</code>. Each period you cover becomes a row.</p>
<h3>How to add a period</h3>
<ol>
  <li>Sign in to the portal on your phone.</li>
  <li>Go to <strong>My Time</strong> &rarr; <strong>My Sub Timesheet</strong>.</li>
  <li>If you''re checked in at a campus today, click <strong>+ Log First Period</strong> on the hero card.</li>
  <li>The period picker opens. Pick the session (Day or Night), then the period number, then the teacher you''re covering.</li>
  <li>Save. The period appears on today''s card.</li>
</ol>
<h3>What gets auto-filled</h3>
<p>When you pick a teacher from the master schedule, the content area, course, and room number auto-populate from the schedule. Confirm those match where you actually went; if the room is wrong, edit it after saving.</p>
<h3>One card per day, per campus</h3>
<p>The page groups all your periods for a date into a single day-card. If you covered classes at Stop Six in the morning and McCart in the evening, those become two separate cards (one per campus).</p>'),

    (N'period-picker', N'substitute-work', N'Substitute Work', 3, 2, N'RequireHourly',
     N'Adding a teacher you covered',
     N'Tips for using the Period Picker — the searchable teacher dropdown.',
     N'<p>The <strong>Period Picker</strong> opens whenever you log a new period. It walks through Session &rarr; Period &rarr; Teacher in that order so the teacher list is always filtered to who is actually scheduled at that time.</p>
<h3>Type to filter</h3>
<p>The teacher list is searchable. Start typing a last name or content area (e.g. "math", "lopez") and the list narrows in real time. Tap the teacher to select them.</p>
<h3>Picking N/A</h3>
<p>Use the yellow <strong>— N/A (Testing / No specific teacher) —</strong> option when you were:</p>
<ul>
  <li>Covering testing — STAAR, MAP, BOY/MOY/EOY benchmarks.</li>
  <li>On general supervision — hallway, lunch duty, ISS, in-house substitute pool.</li>
  <li>Covering a class for a teacher who isn''t in the master schedule (rare, mostly for one-off events).</li>
</ul>
<p>When you pick N/A, type what you were actually doing in the <strong>Notes</strong> field below — payroll uses it to verify the period.</p>
<h3>Already-logged periods are grayed out</h3>
<p>If a period for the active session is already on your card, it shows with a <strong>✓ already logged</strong> tag and can''t be picked again. Edit the existing entry instead.</p>'),

    (N'editing-sub-entries', N'substitute-work', N'Substitute Work', 3, 3, N'RequireHourly',
     N'Editing or removing a logged period',
     N'Fix notes, change a teacher, or remove a period before approval.',
     N'<p>You can edit a period entry as long as the day''s card hasn''t been approved by the campus manager.</p>
<h3>Editing notes</h3>
<ol>
  <li>Open the day card and tap the teacher row to expand the period detail.</li>
  <li>Click the <strong>✎</strong> icon next to the notes field.</li>
  <li>Edit the note. Save.</li>
</ol>
<h3>Removing a period entirely</h3>
<ol>
  <li>Expand the period detail (same path as above).</li>
  <li>Click <strong>Remove</strong>.</li>
  <li>Click <strong>Confirm Remove</strong> on the warning. The entry is deleted and the period number frees up so you can re-add it.</li>
</ol>
<h3>Once the day is approved</h3>
<p>After your campus manager approves the day, edits are locked. If you spot something wrong after approval, file a correction request through your campus manager — same flow as the regular timesheet correction.</p>'),

    (N'sub-timesheet-approval', N'substitute-work', N'Substitute Work', 3, 4, N'RequireHourly',
     N'Sub timesheet approval flow',
     N'How your day-card becomes a paid timecard.',
     N'<p>Your sub timesheet goes through the same staged approval as a regular timesheet, but routed by campus.</p>
<h3>The four states</h3>
<ol>
  <li><strong>Pending</strong> — you''re still adding periods. Day card is editable.</li>
  <li><strong>Submitted</strong> — you''ve submitted the day to your campus manager.</li>
  <li><strong>Approved</strong> — campus manager has reviewed. Day is locked.</li>
  <li><strong>Locked</strong> — the day has been included in a payroll export. No more changes.</li>
</ol>
<h3>Who approves what</h3>
<p>The campus manager at the campus where you checked in approves your day. If you covered classes at both campuses on the same day, you''ll have two separate day cards and each campus manager approves their own.</p>
<h3>Check-in / check-out times</h3>
<p>Your kiosk or mobile check-in time and last clock-out time appear on the day card header. If those look wrong, click <strong>Fix Times</strong> to submit a correction. The campus manager reviews the correction during day approval.</p>'),

    -- ── Sub Requests (RequireAnyStaff — teachers/anyone who might need a sub) ─

    (N'submitting-sub-request', N'sub-requests', N'Sub Requests', 4, 1, N'RequireAnyStaff',
     N'Submitting a substitute request',
     N'Request coverage when you''re going to be out.',
     N'<p>If you''re going to be absent and need someone to cover your classes, submit a sub request from the portal.</p>
<h3>Where</h3>
<p><strong>Mobile</strong> &rarr; <strong>Substitute Request</strong>, or open <code>/employee/absence-request</code>.</p>
<h3>What you''ll fill in</h3>
<ol>
  <li><strong>Absence date(s)</strong> — single day or a range.</li>
  <li><strong>Periods needed</strong> — usually all your periods for the day, but you can pick specific ones (e.g. P1, P3, P5).</li>
  <li><strong>Reason</strong> — Sick, Personal, PD, etc.</li>
  <li><strong>Special instructions for the sub</strong> — lesson plans link, behavior notes, where to find materials. The clearer the better.</li>
  <li><strong>Emergency flag</strong> — only check this for same-day or next-day absences. The system treats Emergency requests with looser day-of cancellation rules so the cascade has more time to find coverage.</li>
</ol>
<h3>Securing coverage yourself first (recommended)</h3>
<p>Before submitting, the form lets you reach out directly to specific subs you''ve worked with before. Pick subs from the list — outreach goes out by email and SMS. Once a sub accepts, the request status flips to <strong>SubConfirmed</strong> and routes to your supervisor for final approval.</p>
<h3>What happens after submit</h3>
<p>You get a confirmation email and SMS summarizing the request and noting any same-day overlaps with other teachers. Your supervisor approves once a sub has accepted all requested periods. See <a href="#tracking-sub-requests">Tracking your sub requests</a>.</p>'),

    (N'tracking-sub-requests', N'sub-requests', N'Sub Requests', 4, 2, N'RequireAnyStaff',
     N'Tracking your sub requests on My Sub Requests',
     N'Status panels for open, partially covered, and approved requests.',
     N'<p>The <strong>My Sub Requests</strong> page (<code>/employee/my-sub-requests</code>) shows every request you''ve submitted, sorted by date.</p>
<h3>The four panels</h3>
<ul>
  <li><strong>Open</strong> — requests still waiting for sub acceptance OR supervisor approval.</li>
  <li><strong>Partially Assigned</strong> — at least one sub has accepted some periods but not all. The panel shows which periods are still uncovered and which sub took which periods.</li>
  <li><strong>Confirmed</strong> — all periods have a sub. Awaiting supervisor approval.</li>
  <li><strong>Approved</strong> — supervisor has approved. You''re done; the request is final.</li>
</ul>
<h3>Why my request flipped sections</h3>
<p>The page auto-flips a request from "Open" to "Approved" once nothing is pending. If your last sub accepts and your supervisor approves all in one afternoon, the request can move panels twice. The <em>Approved</em> panel is the final state.</p>
<h3>Multiple subs on one request</h3>
<p>If different subs covered different periods, all of them are listed in the panel with the periods each took. This happens for partial-day absences and for whole-day requests where one sub couldn''t cover every period.</p>'),

    (N'no-sub-accepted', N'sub-requests', N'Sub Requests', 4, 3, N'RequireAnyStaff',
     N'What happens when no sub accepts',
     N'How outreach cascades, and what to expect if coverage doesn''t land.',
     N'<p>The portal runs a sub-acceptance cascade behind the scenes whenever you submit a request. Subs you know personally get the first round; if no one accepts within a window, the request opens up to the broader sub pool. The cascade keeps fanning out until either someone accepts or the day-of resolution sweep runs.</p>
<h3>While the cascade is running</h3>
<p>You''ll see your request in the <strong>Open</strong> panel of My Sub Requests. The status will be <em>AwaitingSub</em> or <em>SubAssigned</em> depending on whether anyone has tentatively accepted yet. You don''t need to do anything; the system is working.</p>
<h3>If a sub accepts only some periods</h3>
<p>The request moves to <strong>Partially Assigned</strong>. The cascade continues for the remaining periods. If no second sub accepts the rest by 6 AM the next day, your supervisor gets a partial-coverage email with a Take-Over link.</p>
<h3>If nobody accepts at all</h3>
<p>At 6 AM on the absence date, the day-of resolution sweep auto-cancels open requests that have zero acceptances. You and your supervisor both get an email saying so. Talk to your campus manager about same-day options — pulling another teacher off plan, in-house coverage, etc.</p>'),

    (N'day-of-resolution', N'sub-requests', N'Sub Requests', 4, 4, N'RequireAnyStaff',
     N'The 6 AM auto-resolution sweep',
     N'Why some sub requests get auto-approved or auto-cancelled overnight.',
     N'<p>Every morning at 6 AM, the portal walks every sub request whose absence date is today and resolves it based on its current state. This is the <strong>day-of resolution sweep</strong>.</p>
<h3>What happens to your request at 6 AM</h3>
<ul>
  <li>If a sub had accepted all periods but your supervisor hadn''t approved yet, the request is <strong>auto-approved</strong>. You and your supervisor get an email and (if you''re opted in) an SMS.</li>
  <li>If only some periods are covered, the request stays in <em>PartiallyAssigned</em> but your supervisor gets an alert with a Take-Over link so they can manually assign the remaining periods or pull cover.</li>
  <li>If nobody had accepted by then, the request is <strong>auto-cancelled</strong>. You and your supervisor get notified that no substitute was found.</li>
</ul>
<h3>The 24-hour grace window</h3>
<p>If you submitted the request as <em>Emergency</em> in the last 24 hours, the sweep skips it — the cascade gets a fair shot before the system gives up.</p>
<h3>What if the sweep ran when I didn''t want it to?</h3>
<p>Talk to your supervisor or Patrick. The sweep is configurable (run hour, grace window) but only an admin can adjust it.</p>')

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

PRINT '    Seeded Pass 2 part 1 (My Time + Substitute Work + Sub Requests).';
GO

PRINT 'Migration 056 complete.';
GO

/*
===============================================================================
Migration 055 — Seed help articles: Getting Started section
Date: 2026-04-27
Purpose:
  First slice of the help-article seed. Pass 1 ships the system architecture
  with these four "Getting Started" articles so /help renders something
  meaningful on day one. Pass 2 will seed the remaining sections (My Time,
  Substitute Work, Sub Requests, Supervisor, Reception, HR, Admin).

  Idempotent — uses MERGE so re-running this migration is safe and also
  works as the canonical "reset to seed" mechanism. Articles authored by
  Patrick later through the inline edit UI write to ModifiedBy and will NOT
  be overwritten by re-running this seed (the MERGE only updates rows whose
  ModifiedBy IS NULL — i.e. system-seeded rows that were never edited).

Articles in this batch:
  1. signing-in            How to sign in
  2. mobile-checkin        Mobile check-in: clocking in from your phone
  3. sms-phone-update      Updating your phone for SMS notifications
  4. locked-out            I'm locked out — what now?
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 055: Seed Getting Started help articles';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

DECLARE @section_key   NVARCHAR(50)  = N'getting-started';
DECLARE @section_title NVARCHAR(100) = N'Getting Started';
DECLARE @section_order INT           = 1;
DECLARE @policy        NVARCHAR(50)  = N'RequireAnyStaff';
DECLARE @now           DATETIME      = GETDATE();

;WITH src AS (
    SELECT * FROM (VALUES
        (N'signing-in',          1, N'How to sign in',
            N'New Heights Staff Portal sign-in via Microsoft Entra (Azure AD).',
            N'<p>The Staff Portal uses your <strong>New Heights email account</strong> for sign-in. Same credentials you use for Outlook, Teams, and the school computers.</p>
<ol>
  <li>Go to <a href="https://clock.newheightsed.com">clock.newheightsed.com</a> on your phone or computer.</li>
  <li>Click <strong>Sign in</strong> in the top-right corner.</li>
  <li>Enter your <code>@newheightsed.com</code> email address.</li>
  <li>Enter your school password.</li>
  <li>Approve the Microsoft Authenticator prompt on your phone (multi-factor authentication).</li>
</ol>
<p>Your role determines which menu items and home-page cards you see. If something you expect is missing, your Entra group membership may not be configured yet — see <a href="#locked-out">I''m locked out</a>.</p>'),
        (N'mobile-checkin',      2, N'Mobile check-in: clocking in from your phone',
            N'How hourly staff clock in and out using their phones.',
            N'<p>Hourly staff and substitutes clock in and out from <strong>Mobile Check-In</strong> instead of a kiosk. Find it in the left-hand menu under <strong>Mobile</strong> &rarr; <em>Mobile Check-In</em>, or open <code>/mobile/checkin</code> directly.</p>
<h3>How it works</h3>
<ol>
  <li>Open Mobile Check-In on your phone while you''re on campus.</li>
  <li>The page asks for your location once. Allow the prompt.</li>
  <li>If you''re inside the Stop Six or McCart geofence, you''ll see a green status bar with the campus name.</li>
  <li>Tap the big <strong>Check In</strong> (or <strong>Check Out</strong>) button.</li>
</ol>
<h3>If location says you''re not on campus</h3>
<p>Make sure your phone has GPS / location services enabled and you''re actually on campus property. iOS and Android both fall back to coarse Wi-Fi-based location indoors which sometimes drifts; standing near a window for a moment usually nudges it onto GPS.</p>
<p>Admins have a one-click <strong>Force Check-In</strong> override for desktop testing — every other role must be inside the geofence.</p>'),
        (N'sms-phone-update',    3, N'Updating your phone for SMS notifications',
            N'Set or update the phone number that gets text alerts.',
            N'<p>The portal sends text messages for time-sensitive notifications: substitute requests, day-of resolution emails, take-over alerts, and so on. We use the phone number on your employee record.</p>
<h3>To update your number</h3>
<ol>
  <li>Ask Patrick (IT) to update <code>TC_Employees.Phone</code> for your account.</li>
  <li>If you don''t want SMS at all, ask to set <code>SmsOptedOut = 1</code> on your record. You''ll still get email.</li>
</ol>
<h3>Self-service opt-out</h3>
<p>You can opt out at any time by replying <strong>STOP</strong> to any portal text message. To opt back in, ask Patrick to clear the opt-out flag.</p>
<h3>Format</h3>
<p>Numbers are stored without dashes — <code>8175551234</code> works; <code>(817) 555-1234</code> works too. International numbers should include the leading <code>+1</code>.</p>'),
        (N'locked-out',          4, N'I''m locked out — what now?',
            N'What to do when sign-in fails or expected pages are missing.',
            N'<h3>Sign-in fails entirely</h3>
<p>If Microsoft sign-in fails (wrong password, account disabled, MFA loop) you''re hitting the school-wide Entra issue, not a portal issue. Reset your password through Office 365 self-service or contact Patrick.</p>
<h3>You''re signed in but pages are missing</h3>
<p>The portal shows menu items and home-page cards based on your <strong>Entra security group membership</strong>. If a page you expect to see is missing:</p>
<ol>
  <li>Sign out (top-right corner) and sign back in. Group membership changes don''t take effect until your next sign-in.</li>
  <li>Confirm with your supervisor that your account is in the right Entra groups for your role. Examples:
    <ul>
      <li>Hourly employees need <code>TimeClock.Employee.PartTime</code>.</li>
      <li>Supervisors need <code>TimeClock.Supervisor</code> (plus <code>TimeClock.Supervisor.StopSix</code> or <code>TimeClock.Supervisor.McCart</code>).</li>
      <li>Substitutes need <code>TimeClock.Substitute</code>.</li>
    </ul>
  </li>
  <li>If groups look right and pages are still missing, ask Patrick — there may be a sync delay between Entra and the portal.</li>
</ol>
<h3>Hourly tab missing on /my/timesheet</h3>
<p>This is normal for admin-only and salaried-teacher accounts that don''t punch the clock for payroll. The portal shows a friendly "no timesheet for this account" message. Ignore unless you''re actually hourly.</p>')
    ) v(Slug, ArticleOrder, Title, Summary, BodyHtml)
)
MERGE TC_HelpArticles AS tgt
USING src
   ON tgt.Slug = src.Slug
WHEN MATCHED AND tgt.ModifiedBy IS NULL THEN
    UPDATE SET
        Title         = src.Title,
        SectionKey    = @section_key,
        SectionTitle  = @section_title,
        SectionOrder  = @section_order,
        ArticleOrder  = src.ArticleOrder,
        PolicyName    = @policy,
        Summary       = src.Summary,
        BodyHtml      = src.BodyHtml,
        IsActive      = 1,
        ModifiedDate  = @now
WHEN NOT MATCHED THEN
    INSERT (Slug, Title, SectionKey, SectionTitle, SectionOrder, ArticleOrder, PolicyName, Summary, BodyHtml, IsActive, CreatedDate, ModifiedDate, ModifiedBy)
    VALUES (src.Slug, src.Title, @section_key, @section_title, @section_order, src.ArticleOrder, @policy, src.Summary, src.BodyHtml, 1, @now, @now, NULL);

PRINT '    Seeded Getting Started articles (4 rows).';
GO

PRINT 'Migration 055 complete.';
GO

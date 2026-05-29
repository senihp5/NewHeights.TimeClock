using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Web.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/timeclock-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// 2026-04-28 auth restructure — three concerns:
//   1. Staff sign in via Entra OpenIdConnect → "Cookies" cookie scheme
//      (registered by AddMicrosoftIdentityWebApp).
//   2. Students sign in via Google OAuth → DEDICATED "StudentCookie" scheme.
//      Previously Google inherited "Cookies" as its SignInScheme, but
//      Microsoft.Identity.Web wires that scheme with claim validators tuned
//      for Entra principals, which silently rejected the Google-issued
//      principal — the OAuth callback at /signin-google rendered blank
//      instead of redirecting to /student/checkin.
//   3. AppDefault is a PolicyScheme that forwards authentication to either
//      "Cookies" or "StudentCookie" based on which cookie the request
//      carries, so HttpContext.User is populated correctly for both
//      audiences without one stepping on the other. Default challenge
//      stays Entra (staff [Authorize] still triggers the Entra login).
const string StudentCookieScheme = "StudentCookie";
const string AppDefaultScheme = "AppDefault";

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = AppDefaultScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddPolicyScheme(AppDefaultScheme, "Entra-or-Student", options =>
{
    options.ForwardDefaultSelector = ctx =>
    {
        // Student cookie present → authenticate via the student scheme.
        // Otherwise fall through to the staff cookie scheme.
        if (ctx.Request.Cookies.ContainsKey("NHTC.Student"))
            return StudentCookieScheme;
        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
});

// Staff Entra: registers OpenIdConnect (challenge) + "Cookies" (sign-in).
builder.Services.AddAuthentication()
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// Student-specific cookie scheme. Google handler signs into THIS scheme,
// so the staff Cookies scheme's Entra-tuned validators don't see Google
// principals. Cookie name is distinct (NHTC.Student) so the PolicyScheme
// above can route on its presence.
builder.Services.AddAuthentication()
    .AddCookie(StudentCookieScheme, options =>
    {
        options.Cookie.Name = "NHTC.Student";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/student/sign-in";
        options.LogoutPath = "/student/sign-out";
        options.AccessDeniedPath = "/student/sign-in";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

// Phase 8: Google Workspace auth for student self check-in. Only registers
// when Google:Enabled + ClientId + ClientSecret are all present — lets the
// app ship safely before/without the Google Cloud OAuth client being
// provisioned.
var googleEnabled = builder.Configuration.GetValue<bool>("Google:Enabled");
var googleClientId = builder.Configuration["Google:ClientId"];
var googleClientSecret = builder.Configuration["Google:ClientSecret"];
if (googleEnabled
    && !string.IsNullOrWhiteSpace(googleClientId)
    && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    builder.Services.AddAuthentication().AddGoogle(googleOptions =>
    {
        // Critical: sign into the dedicated student cookie scheme, NOT
        // the inherited staff "Cookies" scheme. Without this, the OAuth
        // callback completes on Google's side but the post-callback
        // SignInAsync silently fails against the Entra-tuned cookie
        // scheme and the redirect to /student/checkin never happens.
        googleOptions.SignInScheme = StudentCookieScheme;

        googleOptions.ClientId = googleClientId;
        googleOptions.ClientSecret = googleClientSecret;
        // Default callback path is /signin-google — matches what's configured in
        // Google Cloud Console per Step 8a.3. Google's AddGoogle implementation
        // already maps the "email" user-info field to ClaimTypes.Email, so the
        // RequireStudent policy's User.FindFirst(ClaimTypes.Email) works without
        // an explicit ClaimActions.MapJsonKey call.
        googleOptions.Scope.Add("email");
        googleOptions.Scope.Add("profile");

        // 2026-04-28: Append two Google-specific parameters to the authorize
        // URL. `hd=newheightshs.com` filters the account picker to that
        // Workspace domain (personal Gmail and @newheightsed.com staff
        // accounts are filtered out before sign-in completes). `prompt=
        // select_account` forces the chooser instead of auto-using the
        // device's currently-active Google account, which matters when a
        // student's iPhone is signed into a parent's personal Gmail.
        //
        // GoogleOptions in .NET 8 doesn't expose HostedDomain or
        // AdditionalAuthorizationParameters directly (the latter was added
        // in .NET 9), so we hook OnRedirectToAuthorizationEndpoint and
        // mutate the redirect URL ourselves. Belt-and-suspenders with the
        // RequireStudent policy below — Google enforces at the picker,
        // our policy enforces on the resulting email claim.
        googleOptions.Events.OnRedirectToAuthorizationEndpoint = ctx =>
        {
            var sep = ctx.RedirectUri.Contains('?') ? "&" : "?";
            ctx.Response.Redirect(ctx.RedirectUri + sep + "hd=newheightshs.com&prompt=select_account");
            return Task.CompletedTask;
        };
    });
}

// Request profile scope so optional claims (department, officeLocation)
// arrive in the ID token per the manifest configuration
builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    options.Scope.Add("profile");
    options.Scope.Add("email");
});

builder.Services.AddControllersWithViews()
    .AddMicrosoftIdentityUI();

builder.Services.AddAuthorization(options =>
{
    // Any authenticated Entra ID user with any TimeClock group.
    // Teachers (flat + campus variants) added defensively since the exact
    // role name emitted by their Entra group isn't always uniform — the
    // RequireAssertion prefix check below catches any TimeClock.Teacher.*
    // that we haven't explicitly listed.
    options.AddPolicy("RequireAnyStaff", policy =>
    {
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("TimeClock.AllStaff")
         || ctx.User.IsInRole("TimeClock.Employee")
         || ctx.User.IsInRole("TimeClock.Employee.StopSix")
         || ctx.User.IsInRole("TimeClock.Employee.McCart")
         || ctx.User.IsInRole("TimeClock.Employee.StopSix.PT")
         || ctx.User.IsInRole("TimeClock.Employee.McCart.PT")
         || ctx.User.IsInRole("TimeClock.Substitute")
         || ctx.User.IsInRole("TimeClock.Supervisor")
         || ctx.User.IsInRole("TimeClock.Supervisor.StopSix")
         || ctx.User.IsInRole("TimeClock.Supervisor.McCart")
         || ctx.User.IsInRole("TimeClock.HR")
         || ctx.User.IsInRole("TimeClock.CampusAdmin")
         || ctx.User.IsInRole("TimeClock.Reception")
         || ctx.User.IsInRole("TimeClock.District")
         || ctx.User.IsInRole("TimeClock.Admin")
         // Defensive: any TimeClock.Teacher* role passes. Catches the flat
         // "TimeClock.Teacher" as well as campus variants or any future
         // subcampus-scoped teacher groups we haven't explicitly named yet.
         || ctx.User.Claims.Any(c =>
             c.Type == System.Security.Claims.ClaimTypes.Role
             && c.Value.StartsWith("TimeClock.Teacher", StringComparison.OrdinalIgnoreCase)));
    });

    // Hourly employees and substitutes — can clock in for payroll.
    // Reception is intentionally NOT listed: a receptionist who also punches
    // the clock should be in an Employee / Employee.*.PT Entra group in
    // addition to Reception. RequireAssertion catches any TimeClock.Employee*
    // variant (e.g. new campus-scoped PT groups) without needing a code
    // change each time a new role is introduced.
    options.AddPolicy("RequireHourly", policy =>
    {
        policy.RequireAssertion(ctx =>
            ctx.User.IsInRole("TimeClock.Substitute")
         || ctx.User.IsInRole("TimeClock.Admin")
         // Any TimeClock.Employee* role qualifies — covers the flat Employee,
         // campus variants (StopSix / McCart), and PT variants (.PT suffix).
         || ctx.User.Claims.Any(c =>
             c.Type == System.Security.Claims.ClaimTypes.Role
             && c.Value.StartsWith("TimeClock.Employee", StringComparison.OrdinalIgnoreCase)));
    });

    // Campus-scoped supervisors + admin â€" team timesheets, HR
    options.AddPolicy("RequireSupervisor", policy =>
        policy.RequireRole(
            "TimeClock.Supervisor",
            "TimeClock.Supervisor.StopSix",
            "TimeClock.Supervisor.McCart",
            "TimeClock.Admin"));

    // HR staff â€" approved timesheets only
    options.AddPolicy("RequireHR", policy =>
        policy.RequireRole(
            "TimeClock.HR",
            "TimeClock.Admin"));

    // Campus admins â€" attendance dashboards + reports
    options.AddPolicy("RequireCampusAdmin", policy =>
        policy.RequireRole(
            "TimeClock.CampusAdmin",
            "TimeClock.Supervisor",
            "TimeClock.Supervisor.StopSix",
            "TimeClock.Supervisor.McCart",
            "TimeClock.Reception",
            "TimeClock.Admin"));

    // District staff - all-campus read-only access
    options.AddPolicy("RequireDistrict", policy =>
        policy.RequireRole(
            "TimeClock.District",
            "TimeClock.HR",
            "TimeClock.Admin"));

    // Reception staff - dashboard view only
    options.AddPolicy("RequireReception", policy =>
        policy.RequireRole(
            "TimeClock.Reception",
            "TimeClock.CampusAdmin",
            "TimeClock.Supervisor",
            "TimeClock.Supervisor.StopSix",
            "TimeClock.Supervisor.McCart",
            "TimeClock.Admin"));

    // Admin only
    options.AddPolicy("RequireAdmin", policy =>
        policy.RequireRole("TimeClock.Admin"));

    // Phase 8: Students authenticated via Google Workspace. Policy checks the
    // email claim is @newheightshs.com (students) rather than @newheightsed.com
    // (staff Entra). Only auth'd users who pass the domain check are admitted.
    options.AddPolicy("RequireStudent", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx =>
        {
            var email = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                     ?? ctx.User.FindFirst("email")?.Value
                     ?? "";
            return email.EndsWith("@newheightshs.com", StringComparison.OrdinalIgnoreCase);
        });
    });
});

// Add Blazor services - circuit retention and hub timeouts for Azure App Service
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    });
builder.Services.AddServerSideBlazor(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
    options.DisconnectedCircuitMaxRetained = 100;
    options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(30);
    options.DetailedErrors = builder.Environment.IsDevelopment();
});
builder.Services.AddSignalR();

// Add Entity Framework
builder.Services.AddDbContext<TimeClockDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddDbContextFactory<TimeClockDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")), ServiceLifetime.Scoped);

// Add HttpContextAccessor for accessing user claims
builder.Services.AddHttpContextAccessor();

// Add TimeClock services as Singleton for caching
builder.Services.AddSingleton<IGeofenceService, GeofenceService>();
builder.Services.AddScoped<ITimePunchService, TimePunchService>();
builder.Services.AddScoped<IKioskScanService, KioskScanService>();
builder.Services.AddSingleton<NewHeights.TimeClock.Web.Services.IPhotoThumbnailService,
                              NewHeights.TimeClock.Web.Services.PhotoThumbnailService>();
builder.Services.AddScoped<ITimesheetService, TimesheetService>();
builder.Services.AddScoped<IPayPeriodService, PayPeriodService>();

// User context (campus + role resolution from token claims)
builder.Services.AddScoped<IUserContextService, UserContextService>();
builder.Services.AddSingleton<IGraphService, GraphService>();
builder.Services.AddScoped<IEmployeeSyncService, EmployeeSyncService>();

// Audit log service — writes to TC_AuditLog for all state-changing operations
builder.Services.AddScoped<IAuditService, AuditService>();

// Master schedule lookup — powers the substitute period picker
builder.Services.AddScoped<IMasterScheduleLookupService, MasterScheduleLookupService>();

// Class roster service (Class Attendance Phase B) — cross-DB to CMS for sections + enrollments
builder.Services.AddScoped<IClassRosterService, ClassRosterService>();

// Class attendance service (Class Attendance Phase C) — cell-level writes + sheet workflow
builder.Services.AddScoped<IClassAttendanceService, ClassAttendanceService>();

// Substitute timecard service — sub-facing CRUD for period entries (Phase 2)
builder.Services.AddScoped<ISubstituteTimesheetService, SubstituteTimesheetService>();

// Hourly CSV importer — parses Google-Form weekly timesheet exports into
// suggested punches, then writes TC_TimePunches on admin approval. See
// reference_paper_timesheet_csv_formats.md for the layouts it handles.
builder.Services.AddScoped<IHourlyCsvImportService, HourlyCsvImportService>();

// SMS service — Azure Communication Services wrapper (Phase 6).
// Degrades to no-op when AzureCommunication:Enabled=false or connection string is empty.
builder.Services.AddScoped<ISmsService, AzureSmsService>();

// Substitute outreach service — absence-request sub assignment + accept/decline (Phase 5)
builder.Services.AddScoped<ISubOutreachService, SubOutreachService>();

// In-portal help system — backs /help (migration 054, 2026-04-27).
builder.Services.AddScoped<IHelpArticleService, HelpArticleService>();

// Combined-PDF payroll export (migration 060, 2026-04-27). Uses PDFsharp 6.
builder.Services.AddScoped<IPayrollPdfService, PayrollPdfService>();
// PDFsharp 6 has no default font resolver on Linux; serve bundled Liberation Sans (Defender for Cloud, 2026-05-29).
PdfSharp.Fonts.GlobalFontSettings.FontResolver = new NewHeights.TimeClock.Web.Services.TimeClockFontResolver();

// Stale outreach token expiry job — runs every 4 hours (Phase 7a)
// Phase D2: sub outreach cascade timing (token validity + scan cadence).
// Defaults to 2h token validity + 15min scan interval. Override via
// appsettings section "SubOutreach" without redeploy.
builder.Services.Configure<SubOutreachOptions>(
    builder.Configuration.GetSection("SubOutreach"));
builder.Services.AddHostedService<StaleTokenExpiryService>();

// Phase 9c: escalate stale AwaitingSub requests to campus admin via email + SMS.
// Runs on ScanIntervalHours cadence (default 4h). Disabled via appsettings flag.
// ISubRequestEscalator is the per-request escalation primitive, called by both
// the background service (scheduled) and the admin "Escalate Now" button.
builder.Services.Configure<SubRequestEscalationOptions>(
    builder.Configuration.GetSection("SubRequestEscalation"));
builder.Services.AddScoped<ISubRequestEscalator, SubRequestEscalator>();
builder.Services.AddHostedService<SubRequestEscalationService>();

// Email Service
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, EmailService>();

// Phase D3: timesheet payroll-deadline reminder service. Hourly tick, fires
// 48h + 24h pre-deadline reminders to hourly/PT/sub employees plus a
// SUPERVISOR_DEADLINE notice on/after deadline. Dedup via TC_TimesheetReminderLog
// unique index. Toggle the master switch via appsettings "TimesheetReminder:Enabled".
builder.Services.Configure<TimesheetReminderOptions>(
    builder.Configuration.GetSection("TimesheetReminder"));
builder.Services.AddHostedService<TimesheetReminderService>();

// Phase C (2026-04-27): day-of sub-request resolution sweep. Once per day
// at RunHour (default 6 AM local) every TcSubRequest whose StartDate is
// today and hasn't been finalized is auto-approved (sub confirmed all
// periods), auto-cancelled (no acceptances), or notified to the supervisor
// as a partial-coverage take-over candidate. Toggle via appsettings
// "SubRequestDailyResolution:Enabled".
builder.Services.Configure<SubRequestDailyResolutionOptions>(
    builder.Configuration.GetSection("SubRequestDailyResolution"));
builder.Services.AddHostedService<SubRequestDailyResolutionService>();

// Phase A (migration 048): partial-accept stall alerts. Hourly tick, nudges
// the requesting employee's supervisor once when a PartiallyAssigned request
// hasn't seen progress in ThresholdHours (default 24h). Dedup via
// TC_SubRequests.PartialStallAlertSentAt — reset to NULL whenever a new
// partial accept lands so fresh activity restarts the stall clock.
// Toggle via appsettings "PartialStallAlert:Enabled".
builder.Services.Configure<PartialStallAlertOptions>(
    builder.Configuration.GetSection("PartialStallAlert"));
builder.Services.AddHostedService<PartialStallAlertService>();

// Auto-Checkout Background Service (runs daily at 9:30 PM CST)
builder.Services.AddHostedService<AutoCheckoutService>();
builder.Services.AddScoped<IAutoCheckoutService, AutoCheckoutService>();

var app = builder.Build();

// Pre-warm the campus cache on startup
using (var scope = app.Services.CreateScope())
{
    var geofenceService = scope.ServiceProvider.GetRequiredService<IGeofenceService>();
    try
    {
        var campuses = await geofenceService.GetAllCampusesAsync();
        Log.Information("Pre-cached {Count} campuses on startup", campuses.Count);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to pre-cache campuses on startup");
    }
}

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

// Canonical host redirect: if a request lands on the auto-generated
// Azure App Service host (*.azurewebsites.net), 308-redirect to the
// custom domain clock.newheightsed.com with the same path + query.
// Skipped in Development so local dotnet run / Visual Studio still works.
// Skipped for the ACS webhook + ESP32 /api/v1/punch endpoints because
// those callers can't follow a 30x cleanly.
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var host = context.Request.Host.Host;
        if (host.EndsWith(".azurewebsites.net", StringComparison.OrdinalIgnoreCase))
        {
            var path = context.Request.Path.Value ?? "/";
            // Don't 308 the webhook or punch endpoints - external callers
            // (ACS Event Grid, ESP32 firmware) won't replay POST bodies on
            // redirect, so they need to keep working on the Azure host.
            var preservePaths = new[]
            {
                "/api/webhooks/",
                "/api/v1/punch"
            };
            var shouldPreserve = preservePaths.Any(p =>
                path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!shouldPreserve)
            {
                var query = context.Request.QueryString.HasValue
                    ? context.Request.QueryString.Value
                    : string.Empty;
                var target = $"https://clock.newheightsed.com{path}{query}";
                context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = target;
                return;
            }
        }
        await next();
    });
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapRazorComponents<NewHeights.TimeClock.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.MapControllers();

app.MapPost("/api/v1/punch", async (
    NewHeights.TimeClock.Shared.DTOs.EspScanRequest req,
    NewHeights.TimeClock.Web.Services.IKioskScanService scanService,
    NewHeights.TimeClock.Web.Services.IPhotoThumbnailService thumbnailService,
    Microsoft.EntityFrameworkCore.IDbContextFactory<NewHeights.TimeClock.Data.TimeClockDbContext> dbFactory,
    Microsoft.Extensions.Configuration.IConfiguration config,
    Microsoft.AspNetCore.Http.HttpContext httpCtx,
    Microsoft.Extensions.Logging.ILogger<Program> logger) =>
{
    var configuredSecret = config["EspScanner:DeviceSecret"];
    var headerSecret = httpCtx.Request.Headers["X-Device-Secret"].ToString();
    if (string.IsNullOrEmpty(configuredSecret) || headerSecret != configuredSecret)
    {
        logger.LogWarning("ESP scan rejected — missing or invalid X-Device-Secret. TerminalId={TerminalId}", req?.TerminalId);
        return Microsoft.AspNetCore.Http.Results.Unauthorized();
    }

    if (req == null || string.IsNullOrWhiteSpace(req.RawScan))
    {
        return Microsoft.AspNetCore.Http.Results.BadRequest(new { error = "RawScan required" });
    }

    if (!req.TerminalId.HasValue || req.TerminalId.Value <= 0)
    {
        return Microsoft.AspNetCore.Http.Results.BadRequest(new { error = "TerminalId required" });
    }

    int terminalId = req.TerminalId.Value;
    int campusId;
    int locationId;
    string terminalPurpose;

    using (var ctx = await dbFactory.CreateDbContextAsync())
    {
        var terminal = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(ctx.TcTerminals, t => t.TerminalId == terminalId);

        if (terminal == null)
        {
            logger.LogWarning("ESP scan rejected — TerminalId {TerminalId} not registered", terminalId);
            return Microsoft.AspNetCore.Http.Results.NotFound(new { error = "Terminal not registered" });
        }
        if (!terminal.IsActive)
        {
            logger.LogWarning("ESP scan rejected — TerminalId {TerminalId} ({Code}) is inactive", terminalId, terminal.TerminalCode);
            return Microsoft.AspNetCore.Http.Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        campusId        = terminal.CampusId;
        locationId      = terminal.LocationId;
        terminalPurpose = terminal.TerminalPurpose;

        terminal.LastSeenAt = DateTime.Now;
        try { await ctx.SaveChangesAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to update LastSeenAt for terminal {TerminalId}", terminalId); }
    }

    var scanMethod = string.IsNullOrWhiteSpace(req.ScanMethod) ? "ESP32" : req.ScanMethod!;
    logger.LogInformation("ESP scan accepted — TerminalId={TerminalId} Purpose={Purpose} CampusId={CampusId} LocationId={LocationId} Method={Method}",
        terminalId, terminalPurpose, campusId, locationId, scanMethod);

    var result = await scanService.ProcessRawScanAsync(req.RawScan, campusId, scanMethod, terminalId, locationId);

    // ESP32 kiosks cannot parse the ~770KB JSON the full PhotoBase64 produces
    // (heap exhaustion -> JSON parse FAILED: NoMemory). The wired Blazor kiosk
    // consumes KioskScanService in-process and is unaffected; only HTTP callers
    // reach this lambda. Shrink the photo to a 200x200 q75 JPEG (~15-20 KB base64)
    // so the ESP32 Phase 6 LCD photo display continues to work.
    if (result != null && !string.IsNullOrEmpty(result.PhotoBase64))
    {
        int originalLen = result.PhotoBase64.Length;
        var thumb = thumbnailService.CreateJpegThumbnailBase64(result.PhotoBase64, maxDimensionPx: 200, qualityPercent: 75);
        result.PhotoBase64 = thumb;
        logger.LogInformation("ESP32 punch photo: shrunk {OriginalLen} -> {ThumbLen} base64 chars for TerminalId={TerminalId}",
            originalLen, thumb?.Length ?? 0, terminalId);
    }

    return Microsoft.AspNetCore.Http.Results.Ok(result);
})
.AllowAnonymous();

// Temporary diagnostic endpoint - REMOVE after testing
app.MapGet("/api/test-graph", async (IGraphService graph, IConfiguration config) =>
{
    try
    {
        var tenantId = config["AzureAd:TenantId"];
        var clientId = config["AzureAd:ClientId"];
        var hasSecret = !string.IsNullOrEmpty(config["AzureAd:ClientSecret"]);
        
        var testGroupId = config["GraphSync:EmployeeGroupIds:StopSix"];
        
        var members = await graph.GetGroupMembersAsync(testGroupId ?? "");
        
        return Results.Ok(new
        {
            ConfigCheck = new
            {
                TenantId = tenantId,
                ClientId = clientId,
                HasClientSecret = hasSecret,
                TestGroupId = testGroupId
            },
            MembersFound = members.Count,
            Members = members.Take(3).Select(m => new { m.DisplayName, m.Email })
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            Error = ex.Message,
            Type = ex.GetType().Name,
            InnerError = ex.InnerException?.Message
        });
    }
}).RequireAuthorization();

// Map SignalR hub for real-time dashboard updates
app.MapHub<NewHeights.TimeClock.Web.Hubs.DashboardHub>("/dashboardhub");

app.Run();

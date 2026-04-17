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

// Add Microsoft Identity authentication with MFA enforced by Entra ID
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

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
    // Any authenticated Entra ID user with any TimeClock group
    options.AddPolicy("RequireAnyStaff", policy =>
        policy.RequireRole(
            "TimeClock.AllStaff",
            "TimeClock.Employee",
            "TimeClock.Employee.StopSix",
            "TimeClock.Employee.McCart",
            "TimeClock.Employee.StopSix.PT",
            "TimeClock.Employee.McCart.PT",
            "TimeClock.Substitute",
            "TimeClock.Supervisor",
            "TimeClock.Supervisor.StopSix",
            "TimeClock.Supervisor.McCart",
            "TimeClock.HR",
            "TimeClock.CampusAdmin",
            "TimeClock.Reception",
            "TimeClock.District",
            "TimeClock.Admin"));

    // Hourly employees and substitutes — can clock in for payroll
    options.AddPolicy("RequireHourly", policy =>
        policy.RequireRole(
            "TimeClock.Employee",
            "TimeClock.Employee.StopSix",
            "TimeClock.Employee.McCart",
            "TimeClock.Employee.StopSix.PT",
            "TimeClock.Employee.McCart.PT",
            "TimeClock.Substitute",
            "TimeClock.Admin"));

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

// Substitute timecard service — sub-facing CRUD for period entries (Phase 2)
builder.Services.AddScoped<ISubstituteTimesheetService, SubstituteTimesheetService>();

// SMS service — Azure Communication Services wrapper (Phase 6).
// Degrades to no-op when AzureCommunication:Enabled=false or connection string is empty.
builder.Services.AddScoped<ISmsService, AzureSmsService>();

// Substitute outreach service — absence-request sub assignment + accept/decline (Phase 5)
builder.Services.AddScoped<ISubOutreachService, SubOutreachService>();

// Email Service
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, EmailService>();

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
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapRazorComponents<NewHeights.TimeClock.Web.Components.App>()
    .AddInteractiveServerRenderMode();

app.MapControllers();

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

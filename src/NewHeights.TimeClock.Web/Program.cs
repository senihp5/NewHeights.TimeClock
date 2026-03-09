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
            "TimeClock.Substitute",
            "TimeClock.Supervisor",
            "TimeClock.Supervisor.StopSix",
            "TimeClock.Supervisor.McCart",
            "TimeClock.HR",
            "TimeClock.CampusAdmin",
            "TimeClock.Admin"));

    // Hourly employees and substitutes — can clock in for payroll
    options.AddPolicy("RequireHourly", policy =>
        policy.RequireRole(
            "TimeClock.Employee",
            "TimeClock.Employee.StopSix",
            "TimeClock.Employee.McCart",
            "TimeClock.Substitute"));

    // Campus-scoped supervisors + admin — team timesheets, HR
    options.AddPolicy("RequireSupervisor", policy =>
        policy.RequireRole(
            "TimeClock.Supervisor",
            "TimeClock.Supervisor.StopSix",
            "TimeClock.Supervisor.McCart",
            "TimeClock.Admin"));

    // HR staff — approved timesheets only
    options.AddPolicy("RequireHR", policy =>
        policy.RequireRole(
            "TimeClock.HR",
            "TimeClock.Admin"));

    // Campus admins — attendance dashboards + reports
    options.AddPolicy("RequireCampusAdmin", policy =>
        policy.RequireRole(
            "TimeClock.CampusAdmin",
            "TimeClock.Supervisor",
            "TimeClock.Supervisor.StopSix",
            "TimeClock.Supervisor.McCart",
            "TimeClock.Admin"));

    // Admin only
    options.AddPolicy("RequireAdmin", policy =>
        policy.RequireRole("TimeClock.Admin"));
});

// Add Blazor services
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

// Email Service
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService, EmailService>();

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

app.Run();




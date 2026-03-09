using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace NewHeights.TimeClock.Web.Services;

public interface IEmailService
{
    Task<bool> SendEmailAsync(string to, string subject, string htmlBody);
    Task<bool> SendEmailAsync(List<string> recipients, string subject, string htmlBody);
    Task<bool> SendTimesheetReminderAsync(string employeeEmail, string employeeName, string supervisorName, DateOnly deadline);
    Task<bool> SendSupervisorReminderAsync(string supervisorEmail, string supervisorName, int pendingCount, DateOnly deadline);
    Task<bool> SendApprovalNotificationAsync(string employeeEmail, string employeeName, string approvedBy, string periodName);
}

public class EmailSettings
{
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string Username { get; set; } = "";
    public string AppPassword { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "New Heights Staff Portal";
}

public class EmailService : IEmailService
{
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailService> _logger;

    // Branding colors matching Script Portal
    private const string NavyBlue = "#2D2D6D";
    private const string Gold = "#F7C72C";
    private const string Green = "#28a745";
    private const string Amber = "#ffc107";
    private const string Red = "#dc3545";
    private const string LogoUrl = "https://drive.google.com/uc?export=view&id=1BusU5uMeig4u3gBEafL3nJCmEq75dTuk";

    public EmailService(IOptions<EmailSettings> settings, ILogger<EmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<bool> SendEmailAsync(string to, string subject, string htmlBody)
    {
        return await SendEmailAsync(new List<string> { to }, subject, htmlBody);
    }

    public async Task<bool> SendEmailAsync(List<string> recipients, string subject, string htmlBody)
    {
        try
        {
            using var client = new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
            {
                Credentials = new NetworkCredential(_settings.Username, _settings.AppPassword),
                EnableSsl = true
            };

            var message = new MailMessage
            {
                From = new MailAddress(_settings.FromEmail, _settings.FromName),
                Subject = subject,
                Body = WrapInTemplate(htmlBody),
                IsBodyHtml = true
            };

            foreach (var recipient in recipients.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                message.To.Add(recipient.Trim());
            }

            await client.SendMailAsync(message);
            
            _logger.LogInformation("Email sent successfully to {Recipients}: {Subject}", 
                string.Join(", ", recipients), subject);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Recipients}: {Subject}", 
                string.Join(", ", recipients), subject);
            return false;
        }
    }

    public async Task<bool> SendTimesheetReminderAsync(string employeeEmail, string employeeName, string supervisorName, DateOnly deadline)
    {
        var subject = $"⏰ Time Sheet Reminder - Due {deadline:dddd, MMMM d}";
        
        var body = $@"
            <h2 style='color: {NavyBlue}; margin-bottom: 5px;'>Time Sheet Reminder</h2>
            <p>Hello {employeeName},</p>
            <p>This is a friendly reminder that your <strong>weekly time sheet</strong> is due by:</p>
            <div style='background: {Amber}; color: #000; padding: 15px 25px; border-radius: 8px; display: inline-block; margin: 15px 0;'>
                <strong style='font-size: 18px;'>{deadline:dddd, MMMM d, yyyy}</strong>
            </div>
            <p>Please log in to the Staff Portal and submit your time sheet before the deadline.</p>
            <p style='margin-top: 20px;'>
                <a href='https://timeclock.newheightsed.com/my/timesheet' 
                   style='background: {NavyBlue}; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: bold;'>
                    View My Time Sheet
                </a>
            </p>
            <p style='color: #666; font-size: 14px; margin-top: 25px;'>
                If you have any questions, please contact your supervisor, {supervisorName}.
            </p>";

        return await SendEmailAsync(employeeEmail, subject, body);
    }

    public async Task<bool> SendSupervisorReminderAsync(string supervisorEmail, string supervisorName, int pendingCount, DateOnly deadline)
    {
        var subject = $"📋 {pendingCount} Time Sheets Pending Your Approval";
        
        var body = $@"
            <h2 style='color: {NavyBlue}; margin-bottom: 5px;'>Time Sheets Pending Approval</h2>
            <p>Hello {supervisorName},</p>
            <p>You have <strong>{pendingCount} time sheet(s)</strong> waiting for your review and approval.</p>
            <div style='background: #f0f9ff; border-left: 4px solid {NavyBlue}; padding: 15px; margin: 15px 0;'>
                <p style='margin: 0;'><strong>Employee Deadline:</strong> {deadline:dddd, MMMM d}</p>
                <p style='margin: 5px 0 0;'><strong>Supervisor Approval Due:</strong> {deadline.AddDays(2):dddd, MMMM d}</p>
            </div>
            <p>Please review and approve these time sheets to ensure timely payroll processing.</p>
            <p style='margin-top: 20px;'>
                <a href='https://timeclock.newheightsed.com/supervisor/timesheets' 
                   style='background: {Green}; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; font-weight: bold;'>
                    Review Team Time Sheets
                </a>
            </p>";

        return await SendEmailAsync(supervisorEmail, subject, body);
    }

    public async Task<bool> SendApprovalNotificationAsync(string employeeEmail, string employeeName, string approvedBy, string periodName)
    {
        var subject = $"✓ Time Sheet Approved - {periodName}";
        
        var body = $@"
            <h2 style='color: {Green}; margin-bottom: 5px;'>✓ Time Sheet Approved</h2>
            <p>Hello {employeeName},</p>
            <p>Good news! Your time sheet for <strong>{periodName}</strong> has been approved by your supervisor.</p>
            <div style='background: #f0fdf4; border: 1px solid {Green}; padding: 15px; border-radius: 8px; margin: 15px 0;'>
                <p style='margin: 0;'><strong>Approved By:</strong> {approvedBy}</p>
                <p style='margin: 5px 0 0;'><strong>Approved On:</strong> {DateTime.Now:MMMM d, yyyy 'at' h:mm tt}</p>
            </div>
            <p>Your time sheet has been forwarded to HR for final processing.</p>
            <p style='color: #666; font-size: 14px; margin-top: 20px;'>
                No action is required on your part. If you have questions, please contact HR.
            </p>";

        return await SendEmailAsync(employeeEmail, subject, body);
    }

    private string WrapInTemplate(string bodyContent)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
</head>
<body style='margin: 0; padding: 0; font-family: Arial, sans-serif; background-color: #f5f5f5;'>
    <table width='100%' cellpadding='0' cellspacing='0' style='background-color: #f5f5f5; padding: 20px;'>
        <tr>
            <td align='center'>
                <table width='600' cellpadding='0' cellspacing='0' style='background-color: white; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 8px rgba(0,0,0,0.1);'>
                    <!-- Header -->
                    <tr>
                        <td style='background-color: {NavyBlue}; padding: 20px; text-align: center;'>
                            <img src='{LogoUrl}' alt='New Heights' style='max-height: 50px; background: white; padding: 8px 15px; border-radius: 4px;'>
                        </td>
                    </tr>
                    <!-- Gold accent stripe -->
                    <tr>
                        <td style='background-color: {Gold}; height: 4px;'></td>
                    </tr>
                    <!-- Content -->
                    <tr>
                        <td style='padding: 30px;'>
                            {bodyContent}
                        </td>
                    </tr>
                    <!-- Footer -->
                    <tr>
                        <td style='background-color: {NavyBlue}; padding: 20px; text-align: center;'>
                            <p style='color: white; margin: 0; font-size: 14px;'>
                                New Heights Educational District<br>
                                <span style='color: {Gold};'>Staff Portal</span>
                            </p>
                            <p style='color: #999; margin: 10px 0 0; font-size: 12px;'>
                                This is an automated message. Please do not reply to this email.
                            </p>
                        </td>
                    </tr>
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
    }
}


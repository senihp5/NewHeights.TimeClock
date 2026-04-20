using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Web.Services;

namespace NewHeights.TimeClock.Web.Controllers;

/// <summary>
/// Receives Azure Communication Services SMS delivery-report webhooks via
/// Event Grid (Phase 7d). Endpoint:
///
///   POST https://clock.newheightsed.com/api/webhooks/acs/sms-delivery
///
/// Event Grid setup (Azure Portal):
///   1. ACS resource → Events → + Event Subscription
///   2. Event types: Microsoft.Communication.SMSDeliveryReportReceived
///   3. Endpoint type: Web Hook → URL = above
///   4. On first save, Event Grid sends a SubscriptionValidationEvent which
///      this controller echoes back per the validation handshake.
///
/// Security: [AllowAnonymous] because Event Grid does not authenticate by
/// default. Risk surface is low (we only update DeliveryStatus + audit).
/// Optional hardening: add a shared-secret query string check if abuse appears.
/// </summary>
[ApiController]
[Route("api/webhooks/acs")]
[AllowAnonymous]
public class AcsWebhookController : ControllerBase
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly IAuditService _audit;
    private readonly ILogger<AcsWebhookController> _logger;

    public AcsWebhookController(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IAuditService audit,
        ILogger<AcsWebhookController> logger)
    {
        _dbFactory = dbFactory;
        _audit = audit;
        _logger = logger;
    }

    [HttpPost("sms-delivery")]
    public async Task<IActionResult> OnSmsDelivery([FromBody] JsonElement[] events)
    {
        if (events == null || events.Length == 0)
        {
            _logger.LogDebug("ACS webhook: empty payload — returning 200.");
            return Ok();
        }

        // Step 1: handle Event Grid subscription validation handshake.
        // Sent on first connect; we echo back the validationCode.
        foreach (var evt in events)
        {
            if (TryGetEventType(evt, out var eventType)
                && string.Equals(eventType, "Microsoft.EventGrid.SubscriptionValidationEvent",
                                 StringComparison.OrdinalIgnoreCase))
            {
                if (evt.TryGetProperty("data", out var data)
                    && data.TryGetProperty("validationCode", out var code))
                {
                    var validationCode = code.GetString();
                    _logger.LogInformation(
                        "ACS webhook: Event Grid handshake — echoing validation code.");
                    return Ok(new { validationResponse = validationCode });
                }
            }
        }

        // Step 2: process delivery reports.
        var processedCount = 0;
        foreach (var evt in events)
        {
            try
            {
                if (!TryGetEventType(evt, out var eventType)) continue;

                if (!string.Equals(eventType,
                        "Microsoft.Communication.SMSDeliveryReportReceived",
                        StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "ACS webhook: ignoring event type {Type}", eventType);
                    continue;
                }

                if (!evt.TryGetProperty("data", out var data)) continue;

                var messageId = TryGetString(data, "messageId");
                var deliveryStatus = TryGetString(data, "deliveryStatus");
                var deliveryDetail = TryGetString(data, "deliveryStatusDetails");

                if (string.IsNullOrWhiteSpace(messageId))
                {
                    _logger.LogDebug("ACS webhook: skipping report with empty messageId.");
                    continue;
                }

                using var db = await _dbFactory.CreateDbContextAsync();
                var row = await db.TcSubOutreach
                    .FirstOrDefaultAsync(o => o.MessageId == messageId);
                if (row == null)
                {
                    _logger.LogDebug(
                        "ACS webhook: no TcSubOutreach row for messageId {MessageId}", messageId);
                    continue;
                }

                var normalized = NormalizeDeliveryStatus(deliveryStatus);
                if (row.DeliveryStatus == normalized)
                {
                    // Idempotent — skip if status didn't change.
                    continue;
                }

                var oldStatus = row.DeliveryStatus;
                row.DeliveryStatus = normalized;
                await db.SaveChangesAsync();
                processedCount++;

                if (normalized == "DELIVERED" || normalized == "FAILED")
                {
                    var actionCode = normalized == "DELIVERED"
                        ? AuditActions.SubOutreach.SmsDelivered
                        : AuditActions.SubOutreach.SmsFailed;

                    await _audit.LogActionAsync(
                        actionCode: actionCode,
                        entityType: AuditEntityTypes.SubOutreach,
                        entityId: row.OutreachId.ToString(),
                        oldValues: new { DeliveryStatus = oldStatus },
                        newValues: new
                        {
                            DeliveryStatus = normalized,
                            MessageId = messageId,
                            ReportDetail = deliveryDetail
                        },
                        deltaSummary: $"ACS reported {normalized} for outreach {row.OutreachId} (messageId {messageId})",
                        source: AuditSource.Api,
                        employeeId: row.SubEmployeeId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ACS webhook: error processing one event — continuing with next.");
            }
        }

        if (processedCount > 0)
        {
            _logger.LogInformation(
                "ACS webhook: updated {Count} TcSubOutreach DeliveryStatus rows.",
                processedCount);
        }

        return Ok();
    }

    private static bool TryGetEventType(JsonElement evt, out string eventType)
    {
        eventType = "";
        if (evt.TryGetProperty("eventType", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            eventType = prop.GetString() ?? "";
            return !string.IsNullOrEmpty(eventType);
        }
        return false;
    }

    private static string? TryGetString(JsonElement parent, string propName)
    {
        if (parent.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static string NormalizeDeliveryStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "UNKNOWN";
        return raw.Trim().ToUpperInvariant() switch
        {
            "DELIVERED" => "DELIVERED",
            "FAILED"    => "FAILED",
            "EXPIRED"   => "FAILED",
            "REJECTED"  => "FAILED",
            "STOPPED"   => "FAILED",
            _           => "UNKNOWN"
        };
    }
}

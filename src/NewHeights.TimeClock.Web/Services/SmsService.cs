using Azure;
using Azure.Communication.Sms;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Outcome of a single SMS dispatch attempt. Returned from AzureSmsService.SendAsync
/// so callers (primarily SubOutreachService) can decide whether to audit as
/// SUB_SMS_SENT, SUB_SMS_FAILED, or skip entirely (when SMS is disabled).
/// </summary>
/// <param name="Attempted">False when SMS was skipped (service disabled or config missing). True otherwise.</param>
/// <param name="Delivered">True only when ACS accepted the message. Delivery-to-handset status is separate (Phase 7 webhook).</param>
/// <param name="MessageId">ACS message id when available — stored in TcSubOutreach.MessageId for later correlation.</param>
/// <param name="ErrorReason">Populated when Attempted=true + Delivered=false. Null otherwise.</param>
public record SmsSendResult(bool Attempted, bool Delivered, string? MessageId, string? ErrorReason)
{
    public static SmsSendResult Skipped(string reason) =>
        new(Attempted: false, Delivered: false, MessageId: null, ErrorReason: reason);

    public static SmsSendResult Success(string? messageId) =>
        new(Attempted: true, Delivered: true, MessageId: messageId, ErrorReason: null);

    public static SmsSendResult Failure(string reason) =>
        new(Attempted: true, Delivered: false, MessageId: null, ErrorReason: reason);
}

public interface ISmsService
{
    /// <summary>
    /// True when the service has a connection string AND the AzureCommunication:Enabled
    /// config flag is true. When false, all SendAsync calls return Skipped without hitting
    /// the ACS API. Lets us ship Phase 6 code before toll-free verification lands.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Send one SMS via Azure Communication Services.
    /// </summary>
    /// <param name="toNumber">
    /// Recipient in E.164 format (e.g., "+18175551234"). The service validates shape
    /// but does not enforce a country list — that's a carrier-side concern.
    /// </param>
    /// <param name="body">
    /// Message body. Should include "Reply STOP to opt out" per toll-free compliance.
    /// </param>
    Task<SmsSendResult> SendAsync(string toNumber, string body, CancellationToken ct = default);
}

public class AzureSmsService : ISmsService
{
    private readonly SmsClient? _smsClient;
    private readonly string? _fromNumber;
    private readonly bool _enabledFlag;
    private readonly ILogger<AzureSmsService> _logger;

    public AzureSmsService(IConfiguration configuration, ILogger<AzureSmsService> logger)
    {
        _logger = logger;

        var connectionString = configuration["AzureCommunication:ConnectionString"];
        _fromNumber = configuration["AzureCommunication:SmsFromNumber"];
        _enabledFlag = configuration.GetValue<bool>("AzureCommunication:Enabled");

        if (!_enabledFlag)
        {
            _logger.LogInformation(
                "AzureSmsService: disabled via AzureCommunication:Enabled=false. SMS sends will be skipped.");
            _smsClient = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning(
                "AzureSmsService: Enabled=true but AzureCommunication:ConnectionString is missing. SMS sends will be skipped.");
            _smsClient = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(_fromNumber))
        {
            _logger.LogWarning(
                "AzureSmsService: Enabled=true but AzureCommunication:SmsFromNumber is missing. SMS sends will be skipped.");
            _smsClient = null;
            return;
        }

        try
        {
            _smsClient = new SmsClient(connectionString);
            _logger.LogInformation(
                "AzureSmsService: initialized from number {FromNumber}.", _fromNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AzureSmsService: failed to construct SmsClient. SMS sends will be skipped.");
            _smsClient = null;
        }
    }

    public bool IsEnabled => _smsClient != null
                          && !string.IsNullOrWhiteSpace(_fromNumber);

    public async Task<SmsSendResult> SendAsync(string toNumber, string body, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return SmsSendResult.Skipped("sms-service-disabled");
        }

        if (string.IsNullOrWhiteSpace(toNumber))
        {
            return SmsSendResult.Skipped("missing-recipient-number");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return SmsSendResult.Skipped("empty-body");
        }

        if (!LooksLikeE164(toNumber))
        {
            _logger.LogWarning(
                "AzureSmsService: recipient {ToNumber} is not E.164 format — attempting anyway.",
                toNumber);
        }

        try
        {
            SmsSendResult result;
            var response = await _smsClient!.SendAsync(
                from: _fromNumber,
                to: toNumber,
                message: body,
                options: new SmsSendOptions(enableDeliveryReport: true),
                cancellationToken: ct);

            var smsResult = response.Value;
            if (smsResult.Successful)
            {
                _logger.LogInformation(
                    "AzureSmsService: sent to {To}, messageId={MessageId}",
                    toNumber, smsResult.MessageId);
                result = SmsSendResult.Success(smsResult.MessageId);
            }
            else
            {
                var reason = string.IsNullOrWhiteSpace(smsResult.ErrorMessage)
                    ? $"http-{smsResult.HttpStatusCode}"
                    : smsResult.ErrorMessage;
                _logger.LogWarning(
                    "AzureSmsService: ACS reported failure to {To}. HTTP={Status} Error={Error}",
                    toNumber, smsResult.HttpStatusCode, reason);
                result = SmsSendResult.Failure(reason);
            }
            return result;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "AzureSmsService: ACS request failed for {To}. ErrorCode={Code}",
                toNumber, ex.ErrorCode);
            return SmsSendResult.Failure($"acs-request-failed:{ex.ErrorCode ?? ex.Status.ToString()}");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "AzureSmsService: send to {To} cancelled.", toNumber);
            return SmsSendResult.Failure("cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AzureSmsService: unexpected error sending to {To}.", toNumber);
            return SmsSendResult.Failure($"exception:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Basic E.164 shape check: starts with '+', 8–15 digits after. Not a full validator —
    /// ACS will enforce anyway. Just prevents obviously-malformed inputs from wasting an API call.
    /// </summary>
    private static bool LooksLikeE164(string number)
    {
        if (string.IsNullOrWhiteSpace(number)) return false;
        if (number[0] != '+') return false;
        var digits = number.Substring(1);
        if (digits.Length < 8 || digits.Length > 15) return false;
        foreach (var c in digits)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }
}

using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace NewHeights.TimeClock.Web.Services;

public interface IGraphService
{
    Task<List<GraphUserInfo>> GetGroupMembersAsync(string groupId, CancellationToken ct = default);
    Task<GraphUserInfo?> GetUserManagerAsync(string userId, CancellationToken ct = default);
    Task<List<GraphUserInfo>> GetTransitiveMembersAsync(string groupId, CancellationToken ct = default);
    Task<GraphUserInfo?> GetUserByObjectIdAsync(string objectId, CancellationToken ct = default);
}

public record GraphUserInfo(
    string ObjectId,
    string? DisplayName,
    string? FirstName,
    string? LastName,
    string? Email,
    string? EmployeeId,
    string? Department,
    string? JobTitle
);

public class GraphService : IGraphService
{
    private readonly IConfiguration _config;
    private readonly ILogger<GraphService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private GraphServiceClient? _graphClient;

    public GraphService(IConfiguration config, ILogger<GraphService> logger)
    {
        _config = config;
        _logger = logger;
    }

    private Task<GraphServiceClient> GetClientAsync()
    {
        if (_graphClient != null) return Task.FromResult(_graphClient);
        _lock.Wait();
        try
        {
            if (_graphClient != null) return Task.FromResult(_graphClient);
            var tenantId     = _config["AzureAd:TenantId"]     ?? throw new InvalidOperationException("AzureAd:TenantId missing");
            var clientId     = _config["AzureAd:ClientId"]     ?? throw new InvalidOperationException("AzureAd:ClientId missing");
            var clientSecret = _config["AzureAd:ClientSecret"] ?? throw new InvalidOperationException("AzureAd:ClientSecret missing");
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            _graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
            _logger.LogInformation("GraphServiceClient initialized for tenant {TenantId}", tenantId);
            return Task.FromResult(_graphClient);
        }
        finally { _lock.Release(); }
    }

    public async Task<List<GraphUserInfo>> GetGroupMembersAsync(string groupId, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        var results = new List<GraphUserInfo>();
        try
        {
            _logger.LogInformation("Fetching members for group {GroupId}", groupId);
            var members = await client.Groups[groupId].Members
                .GetAsync(req =>
                {
                    req.QueryParameters.Select = new[] { "id", "displayName", "givenName", "surname", "mail", "userPrincipalName", "employeeId", "department", "jobTitle" };
                    req.QueryParameters.Top = 999;
                }, ct);

            var page = members;
            while (page?.Value != null)
            {
                foreach (var member in page.Value.OfType<User>())
                    results.Add(MapUser(member));
                if (page.OdataNextLink == null) break;
                page = await client.Groups[groupId].Members
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct);
            }
            _logger.LogInformation("Group {GroupId}: retrieved {Count} members", groupId, results.Count);
        }
        catch (ODataError ode)
        {
            _logger.LogError(ode, "Graph API error for group {GroupId}: {Code} - {Message}", groupId, ode.Error?.Code, ode.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting members for group {GroupId}: {Type}", groupId, ex.GetType().Name);
        }
        return results;
    }

    public async Task<List<GraphUserInfo>> GetTransitiveMembersAsync(string groupId, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        var results = new List<GraphUserInfo>();
        try
        {
            _logger.LogInformation("Fetching transitive members for group {GroupId}", groupId);
            var members = await client.Groups[groupId].TransitiveMembers
                .GetAsync(req =>
                {
                    req.QueryParameters.Select = new[] { "id", "displayName", "givenName", "surname", "mail", "userPrincipalName", "employeeId", "department", "jobTitle" };
                    req.QueryParameters.Top = 999;
                }, ct);
            var page = members;
            while (page?.Value != null)
            {
                foreach (var member in page.Value.OfType<User>())
                    results.Add(MapUser(member));
                if (page.OdataNextLink == null) break;
                page = await client.Groups[groupId].TransitiveMembers
                    .WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct);
            }
            _logger.LogInformation("Group {GroupId} transitive: retrieved {Count} members", groupId, results.Count);
        }
        catch (ODataError ode)
        {
            _logger.LogError(ode, "Graph API error (transitive) for group {GroupId}: {Code} - {Message}", groupId, ode.Error?.Code, ode.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting transitive members for group {GroupId}", groupId);
        }
        return results;
    }

    public async Task<GraphUserInfo?> GetUserManagerAsync(string userId, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        try
        {
            var manager = await client.Users[userId].Manager.GetAsync(cancellationToken: ct);
            if (manager is User u) return MapUser(u);
        }
        catch (ODataError ode) when (ode.Error?.Code == "Request_ResourceNotFound") { }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not get manager for user {UserId}", userId); }
        return null;
    }

    public async Task<GraphUserInfo?> GetUserByObjectIdAsync(string objectId, CancellationToken ct = default)
    {
        var client = await GetClientAsync();
        try
        {
            var user = await client.Users[objectId].GetAsync(req =>
            {
                req.QueryParameters.Select = new[] { "id", "displayName", "givenName", "surname", "mail", "userPrincipalName", "employeeId", "department", "jobTitle" };
            }, ct);
            return user != null ? MapUser(user) : null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not get user {ObjectId}", objectId); }
        return null;
    }

    private static GraphUserInfo MapUser(User u) => new(
        ObjectId:    u.Id ?? "",
        DisplayName: u.DisplayName,
        FirstName:   u.GivenName,
        LastName:    u.Surname,
        Email:       u.Mail ?? u.UserPrincipalName,
        EmployeeId:  u.EmployeeId,
        Department:  u.Department,
        JobTitle:    u.JobTitle
    );
}

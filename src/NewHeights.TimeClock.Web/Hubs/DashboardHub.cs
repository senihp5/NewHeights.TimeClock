using Microsoft.AspNetCore.SignalR;

namespace NewHeights.TimeClock.Web.Hubs;

public class DashboardHub : Hub
{
    public async Task NotifyScanCompleted(string campusCode)
    {
        await Clients.Group($"Dashboard_{campusCode}").SendAsync("ScanCompleted");
    }

    public async Task JoinDashboard(string campusCode)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"Dashboard_{campusCode}");
    }

    public async Task LeaveDashboard(string campusCode)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Dashboard_{campusCode}");
    }
}

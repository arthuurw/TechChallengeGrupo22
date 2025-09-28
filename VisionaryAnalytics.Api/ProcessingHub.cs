using Microsoft.AspNetCore.SignalR;

namespace VisionaryAnalytics.Api;

public class ProcessingHub : Hub
{
    // Cliente pode entrar em um "grupo" por jobId para receber eventos
    public Task SubscribeToJob(Guid jobId)
        => Groups.AddToGroupAsync(Context.ConnectionId, jobId.ToString());

    // Método chamado pelo Worker (via SignalR Client)
    public async Task NotifyCompleted(Guid jobId, int resultsCount)
        => await Clients.Group(jobId.ToString())
            .SendAsync("processingCompleted", new { jobId, resultsCount });
}
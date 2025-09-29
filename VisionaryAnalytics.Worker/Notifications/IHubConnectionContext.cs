using Microsoft.AspNetCore.SignalR.Client;

namespace VisionaryAnalytics.Worker.Notifications;

public interface IHubConnectionContext : IAsyncDisposable
{
    HubConnectionState State { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task InvokeAsync(string methodName, object? arg1, object? arg2, CancellationToken cancellationToken = default);
}

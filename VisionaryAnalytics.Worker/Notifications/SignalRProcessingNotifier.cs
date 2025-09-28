using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VisionaryAnalytics.Worker.Notifications;

public sealed class SignalRProcessingNotifier : IProcessingNotifier, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<SignalRProcessingNotifier> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public SignalRProcessingNotifier(IConfiguration configuration, ILogger<SignalRProcessingNotifier> logger)
    {
        _logger = logger;
        var hubUrl = configuration["SIGNALR__HUB_URL"]
            ?? configuration.GetSection("SignalR")["HubUrl"]
            ?? "http://localhost:8080/hubs/processing";

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();
    }

    public async Task NotifyCompletedAsync(Guid jobId, int resultsCount, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await _connection.InvokeAsync("NotifyCompleted", jobId, resultsCount, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify completion for job {JobId}", jobId);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection.State == HubConnectionState.Connected ||
            _connection.State == HubConnectionState.Connecting ||
            _connection.State == HubConnectionState.Reconnecting)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _connectionLock.Dispose();
    }
}

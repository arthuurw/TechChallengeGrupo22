using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using VisionaryAnalytics.Worker.Options;

namespace VisionaryAnalytics.Worker.Notifications;

public sealed class SignalRProcessingNotifier : IProcessingNotifier
{
    private readonly ILogger<SignalRProcessingNotifier> _logger;
    private readonly SignalROptions _options;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private HubConnection? _connection;

    public SignalRProcessingNotifier(IOptions<SignalROptions> options, ILogger<SignalRProcessingNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    public async Task NotifyCompletedAsync(Guid jobId, int resultsCount, CancellationToken cancellationToken = default)
    {
        if (!await TryEnsureConnectionAsync(cancellationToken))
        {
            return;
        }

        try
        {
            await _connection!.InvokeAsync("NotifyCompleted", jobId, resultsCount, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao notificar a conclusão do job {JobId}", jobId);
        }
    }

    public async Task NotifyFailedAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default)
    {
        if (!await TryEnsureConnectionAsync(cancellationToken))
        {
            return;
        }

        try
        {
            await _connection!.InvokeAsync("NotifyFailed", jobId, errorMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao notificar a falha do job {JobId}", jobId);
        }
    }

    private async Task<bool> TryEnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (!_options.EnableNotifications)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.HubUrl))
        {
            _logger.LogDebug("URL do hub SignalR não configurada; notificações serão ignoradas");
            return false;
        }

        if (_connection is { State: HubConnectionState.Connected })
        {
            return true;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is null)
            {
                _connection = new HubConnectionBuilder()
                    .WithUrl(_options.HubUrl)
                    .WithAutomaticReconnect()
                    .Build();
            }

            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(cancellationToken);
            }

            return _connection.State == HubConnectionState.Connected;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível conectar ao hub SignalR em {HubUrl}", _options.HubUrl);
            return false;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _connectionLock.Dispose();
    }
}

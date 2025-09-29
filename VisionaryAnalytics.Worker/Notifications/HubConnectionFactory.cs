using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.SignalR.Client;

namespace VisionaryAnalytics.Worker.Notifications;

[ExcludeFromCodeCoverage]
public sealed class HubConnectionFactory : IHubConnectionFactory
{
    public IHubConnectionContext Create(string hubUrl)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        return new HubConnectionContext(connection);
    }

    private sealed class HubConnectionContext : IHubConnectionContext
    {
        private readonly HubConnection _connection;

        public HubConnectionContext(HubConnection connection)
        {
            _connection = connection;
        }

        public HubConnectionState State => _connection.State;

        public Task StartAsync(CancellationToken cancellationToken = default)
            => _connection.StartAsync(cancellationToken);

        public Task InvokeAsync(string methodName, object? arg1, object? arg2, CancellationToken cancellationToken = default)
            => _connection.InvokeAsync(methodName, arg1, arg2, cancellationToken);

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}

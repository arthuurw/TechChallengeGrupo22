namespace VisionaryAnalytics.Infrastructure.Interface;

public interface IRabbitMqPublisher : IAsyncDisposable
{
    Task PublishAsync<T>(T message, CancellationToken cancellationToken = default);
}

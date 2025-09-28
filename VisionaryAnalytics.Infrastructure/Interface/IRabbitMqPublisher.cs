using System.Threading;

namespace VisionaryAnalytics.Infrastructure.Interface;

public interface IRabbitMqPublisher
{
    Task PublishAsync<T>(T message, CancellationToken cancellationToken = default);
}

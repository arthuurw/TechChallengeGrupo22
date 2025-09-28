using VisionaryAnalytics.Infrastructure.Messaging;

namespace VisionaryAnalytics.Worker.Processing;

public interface IVideoJobProcessor
{
    Task<int> ProcessAsync(VideoJobMessage message, CancellationToken cancellationToken);
}

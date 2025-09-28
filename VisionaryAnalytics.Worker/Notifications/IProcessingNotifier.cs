namespace VisionaryAnalytics.Worker.Notifications;

public interface IProcessingNotifier
{
    Task NotifyCompletedAsync(Guid jobId, int resultsCount, CancellationToken cancellationToken = default);
}

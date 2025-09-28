namespace VisionaryAnalytics.Worker.Notifications;

public interface IProcessingNotifier : IAsyncDisposable
{
    Task NotifyCompletedAsync(Guid jobId, int resultsCount, CancellationToken cancellationToken = default);
    Task NotifyFailedAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken = default);
}

namespace VisionaryAnalytics.Infrastructure.Interface;

public interface IVideoJobStore
{
    Task InitAsync(Guid jobId, string fileName, double fps);
    Task SetStatusAsync(Guid jobId, string status);
    Task<string?> GetStatusAsync(Guid jobId);
    Task AddResultAsync(Guid jobId, string content, double timestampSec);
    Task<object> GetResultsAsync(Guid jobId);
}
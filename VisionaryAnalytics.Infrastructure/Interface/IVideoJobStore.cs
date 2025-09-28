using VisionaryAnalytics.Infrastructure.Models;

namespace VisionaryAnalytics.Infrastructure.Interface;

public interface IVideoJobStore
{
    Task InitAsync(Guid jobId, string fileName, double fps, CancellationToken cancellationToken = default);
    Task SetStatusAsync(Guid jobId, string status, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task<VideoJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task AddResultAsync(Guid jobId, VideoJobResult result, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VideoJobResult>> GetResultsAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<VideoJobMetadata?> GetMetadataAsync(Guid jobId, CancellationToken cancellationToken = default);
}

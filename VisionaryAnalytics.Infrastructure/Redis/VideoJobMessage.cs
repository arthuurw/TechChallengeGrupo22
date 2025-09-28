namespace VisionaryAnalytics.Infrastructure.Redis;

public record VideoJobMessage
    (
        Guid JobId,
        string FilePath,
        double Fps,
        string? CorrelationId = null
    );
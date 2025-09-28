using System;

namespace VisionaryAnalytics.Infrastructure.Redis;

public sealed record VideoJobMessage(
    Guid JobId,
    string FilePath,
    double Fps,
    string? CorrelationId = null
);

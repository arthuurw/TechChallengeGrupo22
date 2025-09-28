namespace VisionaryAnalytics.Infrastructure.Messaging;

public sealed record VideoJobMessage(Guid JobId, string FilePath, double Fps, string? CorrelationId = null);

namespace VisionaryAnalytics.Infrastructure.Models;

public sealed record VideoJobMetadata(string FileName, double FramesPerSecond, DateTimeOffset CreatedAt);

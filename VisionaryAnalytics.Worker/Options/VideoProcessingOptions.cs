namespace VisionaryAnalytics.Worker.Options;

public sealed class VideoProcessingOptions
{
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;
}

using System;

namespace VideoAnalyzer.Shared;

public record VideoProcessingMessage(Guid VideoId, string FilePath);

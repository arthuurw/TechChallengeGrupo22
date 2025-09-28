using System.Collections.Concurrent;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VisionaryAnalytics.Infrastructure;
using VisionaryAnalytics.Infrastructure.Redis;
using ZXing;
using ZXing.ImageSharp;

namespace VisionaryAnalytics.Worker.Processing;

public sealed class VideoProcessingService
{
    private readonly ILogger<VideoProcessingService> _logger;
    private readonly BarcodeReader<Rgba32> _reader;

    public VideoProcessingService(ILogger<VideoProcessingService> logger)
    {
        _logger = logger;
        _reader = new BarcodeReader<Rgba32>
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions
            {
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
                TryHarder = true,
                ReturnCodabarStartEnd = true
            }
        };
    }

    public async Task<IReadOnlyList<VideoJobResult>> AnalyzeAsync(VideoJobMessage job, CancellationToken cancellationToken)
    {
        var frameRate = job.Fps <= 0 ? 1d : job.Fps;
        var tempDir = Path.Combine(Path.GetTempPath(), "va-frames", job.JobId.ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var pattern = Path.Combine(tempDir, "frame_%06d.png");
            await FFMpegArguments
                .FromFileInput(job.FilePath)
                .OutputToFile(pattern, overwrite: true, options => options
                    .WithVideoCodec("png")
                    .WithFrameRate(frameRate)
                    .ForceFormat("image2"))
                .ProcessAsynchronously(true, cancellationToken)
                .ConfigureAwait(false);

            var files = Directory
                .EnumerateFiles(tempDir, "frame_*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select((path, index) => (path, index))
                .ToArray();

            if (files.Length == 0)
            {
                _logger.LogWarning("No frames extracted for job {JobId}", job.JobId);
                return Array.Empty<VideoJobResult>();
            }

            var results = new ConcurrentBag<VideoJobResult>();
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            };

            Parallel.ForEach(files, parallelOptions, frame =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var image = Image.Load<Rgba32>(frame.path);
                    var decoded = _reader.Decode(image);
                    if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
                    {
                        return;
                    }

                    var timestamp = Math.Round(frame.index / frameRate, 3, MidpointRounding.AwayFromZero);
                    results.Add(new VideoJobResult(decoded.Text, timestamp));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to analyze frame {Frame} for job {JobId}", frame.path, job.JobId);
                }
            });

            return results
                .OrderBy(r => r.TimestampSeconds)
                .ToArray();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Failed to clean temporary directory for job {JobId}", job.JobId);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Unauthorized deleting temporary directory for job {JobId}", job.JobId);
            }
        }
    }
}

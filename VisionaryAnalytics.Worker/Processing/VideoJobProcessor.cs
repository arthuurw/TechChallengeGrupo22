using System.Collections.Concurrent;
using FFMpegCore;
using FFMpegCore.Enums;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Messaging;
using VisionaryAnalytics.Infrastructure.Models;
using VisionaryAnalytics.Worker.Notifications;
using VisionaryAnalytics.Worker.Options;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace VisionaryAnalytics.Worker.Processing;

public sealed class VideoJobProcessor : IVideoJobProcessor
{
    private readonly ILogger<VideoJobProcessor> _logger;
    private readonly IVideoJobStore _store;
    private readonly IProcessingNotifier _notifier;
    private readonly VideoProcessingOptions _options;

    public VideoJobProcessor(
        ILogger<VideoJobProcessor> logger,
        IVideoJobStore store,
        IProcessingNotifier notifier,
        IOptions<VideoProcessingOptions> optionsAccessor)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(optionsAccessor);

        _logger = logger;
        _store = store;
        _notifier = notifier;
        _options = optionsAccessor.Value;
    }

    public async Task<int> ProcessAsync(VideoJobMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!File.Exists(message.FilePath))
        {
            const string errorMessage = "Arquivo de vídeo não encontrado para processamento";
            await _store.SetStatusAsync(message.JobId, VideoJobStatuses.Failed, errorMessage, cancellationToken);
            await _notifier.NotifyFailedAsync(message.JobId, errorMessage, cancellationToken);
            _logger.LogWarning("Arquivo de vídeo {FilePath} não foi encontrado para o job {JobId}", message.FilePath, message.JobId);
            return 0;
        }

        await _store.SetStatusAsync(message.JobId, VideoJobStatuses.Processing, cancellationToken: cancellationToken);

        var extractionDirectory = Path.Combine(Path.GetTempPath(), $"frames_{message.JobId:N}");
        Directory.CreateDirectory(extractionDirectory);

        try
        {
            await ExtractFramesAsync(message, extractionDirectory, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var frames = Directory.EnumerateFiles(extractionDirectory, "frame_*.png")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select((path, index) => new FrameDescriptor(path, index))
                .ToArray();

            if (frames.Length == 0)
            {
                _logger.LogInformation("Nenhum quadro foi extraído para o job {JobId}", message.JobId);
            }

            var results = new ConcurrentBag<VideoJobResult>();
            var degreeOfParallelism = _options.MaxDegreeOfParallelism > 0
                ? _options.MaxDegreeOfParallelism
                : Environment.ProcessorCount;

            await Parallel.ForEachAsync(frames, new ParallelOptions
            {
                MaxDegreeOfParallelism = degreeOfParallelism,
                CancellationToken = cancellationToken
            }, async (frame, token) =>
            {
                var result = await TryDecodeFrameAsync(frame.Path, frame.Index, message.Fps, token);
                if (result is not null)
                {
                    results.Add(result);
                }
            });

            var orderedResults = results
                .OrderBy(result => result.InstanteSegundos)
                .ToList();

            foreach (var result in orderedResults)
            {
                await _store.AddResultAsync(message.JobId, result, cancellationToken);
            }

            await _store.SetStatusAsync(message.JobId, VideoJobStatuses.Completed, cancellationToken: cancellationToken);
            await _notifier.NotifyCompletedAsync(message.JobId, orderedResults.Count, cancellationToken);

            _logger.LogInformation("Job {JobId} concluído. QR codes encontrados: {Count}", message.JobId, orderedResults.Count);

            return orderedResults.Count;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Processamento cancelado para o job {JobId}", message.JobId);
            throw;
        }
        catch (Exception ex)
        {
            var safeMessage = ex.Message;
            _logger.LogError(ex, "Erro ao processar vídeo para o job {JobId}", message.JobId);
            await _store.SetStatusAsync(message.JobId, VideoJobStatuses.Failed, safeMessage, cancellationToken);
            await _notifier.NotifyFailedAsync(message.JobId, safeMessage, cancellationToken);
            throw;
        }
        finally
        {
            TryCleanup(extractionDirectory);
        }
    }

    private static async Task ExtractFramesAsync(VideoJobMessage message, string outputDirectory, CancellationToken cancellationToken)
    {
        var framePattern = Path.Combine(outputDirectory, "frame_%05d.png");
        var arguments = FFMpegArguments
            .FromFileInput(message.FilePath)
            .OutputToFile(framePattern, overwrite: true, options => options
                .WithVideoCodec(VideoCodec.Png)
                .WithFrameRate(message.Fps));

        await arguments.ProcessAsynchronously(true, cancellationToken);
    }

    private static async ValueTask<VideoJobResult?> TryDecodeFrameAsync(string path, int index, double fps, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var image = await Image.LoadAsync<Rgba32>(stream, cancellationToken);
        var luminance = ExtractLuminance(image);
        var bitmap = new BinaryBitmap(new HybridBinarizer(luminance));

        try
        {
            var reader = new QRCodeReader();
            var decodeResult = reader.decode(bitmap);
            if (decodeResult is null)
            {
                return null;
            }

            var timestamp = Math.Round(index / Math.Max(fps, 1d), 3);
            return new VideoJobResult(decodeResult.Text, timestamp);
        }
        catch (ReaderException)
        {
            return null;
        }
    }

    private static RGBLuminanceSource ExtractLuminance(Image<Rgba32> image)
    {
        var buffer = new byte[image.Width * image.Height];
        var offset = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var rowSpan = accessor.GetRowSpan(y);
                for (var x = 0; x < rowSpan.Length; x++)
                {
                    var pixel = rowSpan[x];
                    buffer[offset++] = (byte)Math.Clamp((pixel.R * 0.299) + (pixel.G * 0.587) + (pixel.B * 0.114), 0, 255);
                }
            }
        });

        return new RGBLuminanceSource(buffer, image.Width, image.Height);
    }

    private static void TryCleanup(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Ignored on purpose.
        }
    }

    private sealed record FrameDescriptor(string Path, int Index);
}

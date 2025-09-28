using StackExchange.Redis;
using System.Text.Json;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Models;

namespace VisionaryAnalytics.Infrastructure.Redis;

public class RedisVideoJobStore(IConnectionMultiplexer multiplexer) : IVideoJobStore
{
    private readonly IConnectionMultiplexer _multiplexer = multiplexer;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task InitAsync(Guid jobId, string fileName, double fps, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        await db.StringSetAsync(KeyStatus(jobId), VideoJobStatuses.Queued);
        await db.KeyDeleteAsync(KeyError(jobId));
        await db.KeyDeleteAsync(KeyResults(jobId));
        await db.HashSetAsync(KeyMeta(jobId),
            new HashEntry[]
            {
                new("nomeArquivo", fileName),
                new("fps", fps.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)),
                new("criadoEm", DateTimeOffset.UtcNow.ToString("O"))
            });
    }

    public Task SetStatusAsync(Guid jobId, string status, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var tasks = new List<Task>
        {
            db.StringSetAsync(KeyStatus(jobId), status)
        };

        tasks.Add(!string.IsNullOrWhiteSpace(errorMessage)
            ? db.StringSetAsync(KeyError(jobId), errorMessage)
            : db.KeyDeleteAsync(KeyError(jobId)));

        return Task.WhenAll(tasks);
    }

    public async Task<VideoJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var status = await db.StringGetAsync(KeyStatus(jobId));
        if (status.IsNullOrEmpty)
        {
            return null;
        }

        var error = await db.StringGetAsync(KeyError(jobId));
        return new VideoJobState(status!, error.IsNullOrEmpty ? null : error.ToString());
    }

    public async Task AddResultAsync(Guid jobId, VideoJobResult result, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var payload = JsonSerializer.Serialize(result, SerializerOptions);
        await db.SortedSetAddAsync(KeyResults(jobId), payload, result.InstanteSegundos);
    }

    public async Task<IReadOnlyList<VideoJobResult>> GetResultsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var entries = await db.SortedSetRangeByRankAsync(KeyResults(jobId), 0, -1, Order.Ascending);
        if (entries.Length == 0)
        {
            return Array.Empty<VideoJobResult>();
        }

        var results = new List<VideoJobResult>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.IsNullOrEmpty)
            {
                continue;
            }

            if (JsonSerializer.Deserialize<VideoJobResult>(entry!, SerializerOptions) is { } result)
            {
                results.Add(result);
            }
        }

        return results;
    }

    public async Task<VideoJobMetadata?> GetMetadataAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var db = _multiplexer.GetDatabase();
        var values = await db.HashGetAllAsync(KeyMeta(jobId));
        if (values.Length == 0)
        {
            return null;
        }

        var map = values.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());
        if (!map.TryGetValue("nomeArquivo", out var fileName) || !map.TryGetValue("fps", out var fpsRaw))
        {
            return null;
        }

        var fps = double.TryParse(fpsRaw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedFps)
            ? parsedFps
            : 0d;

        var createdAt = map.TryGetValue("criadoEm", out var createdAtRaw) && DateTimeOffset.TryParse(createdAtRaw, out var parsedCreatedAt)
            ? parsedCreatedAt
            : DateTimeOffset.MinValue;

        return new VideoJobMetadata(fileName, fps, createdAt);
    }

    private static string KeyStatus(Guid id) => $"job:{id}:status";
    private static string KeyError(Guid id) => $"job:{id}:error";
    private static string KeyMeta(Guid id) => $"job:{id}:meta";
    private static string KeyResults(Guid id) => $"job:{id}:results";
}

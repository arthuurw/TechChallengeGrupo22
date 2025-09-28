using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StackExchange.Redis;
using VisionaryAnalytics.Infrastructure.Interface;

namespace VisionaryAnalytics.Infrastructure.Redis;

public class RedisVideoJobStore(IConnectionMultiplexer mux) : IVideoJobStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IConnectionMultiplexer _mux = mux;

    public async Task InitAsync(Guid jobId, string fileName, double fps)
    {
        var db = _mux.GetDatabase();
        var safeFileName = Path.GetFileName(fileName);
        await db.StringSetAsync(KeyStatus(jobId), "NaFila");
        await db.HashSetAsync(KeyMeta(jobId), new HashEntry[]
        {
            new("nomeArquivo", safeFileName),
            new("fps", fps.ToString()),
            new("criadoEm", DateTimeOffset.UtcNow.ToString("O"))
        });
        await db.KeyDeleteAsync(KeyResults(jobId));
    }

    public Task SetStatusAsync(Guid jobId, string status)
        => _mux.GetDatabase().StringSetAsync(KeyStatus(jobId), status);

    public async Task<string?> GetStatusAsync(Guid jobId)
    {
        var value = await _mux.GetDatabase().StringGetAsync(KeyStatus(jobId));
        return value.HasValue ? value.ToString() : null;
    }

    public Task AddResultAsync(Guid jobId, string content, double timestampSec)
    {
        var db = _mux.GetDatabase();
        var payload = JsonSerializer.Serialize(new VideoJobResult(content, timestampSec), SerializerOptions);
        return db.SortedSetAddAsync(KeyResults(jobId), payload, timestampSec);
    }

    public async Task<IReadOnlyList<VideoJobResult>> GetResultsAsync(Guid jobId)
    {
        var db = _mux.GetDatabase();
        var arr = await db.SortedSetRangeByRankAsync(KeyResults(jobId), 0, -1, Order.Ascending);
        var results = new List<VideoJobResult>(arr.Length);
        foreach (var entry in arr)
        {
            if (!entry.HasValue) continue;
            var result = JsonSerializer.Deserialize<VideoJobResult>(entry!, SerializerOptions);
            if (result is not null)
            {
                results.Add(result);
            }
        }
        return results;
    }

    private static string KeyStatus(Guid id) => $"job:{id}:status";

    private static string KeyMeta(Guid id) => $"job:{id}:meta";

    private static string KeyResults(Guid id) => $"job:{id}:results";
}

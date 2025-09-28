using StackExchange.Redis;
using System.Text.Json;
using VisionaryAnalytics.Infrastructure.Interface;

namespace VisionaryAnalytics.Infrastructure;

public class RedisVideoJobStore(IConnectionMultiplexer mux) : IVideoJobStore
{
    private readonly IConnectionMultiplexer _mux = mux;

    public async Task InitAsync(Guid jobId, string fileName, double fps)
    {
        var db = _mux.GetDatabase();
        await db.StringSetAsync(KeyStatus(jobId), "Queued");
        await db.HashSetAsync(KeyMeta(jobId), new HashEntry[] {
            new("filename", fileName), new("fps", fps.ToString()),
            new("createdAt", DateTimeOffset.UtcNow.ToString("O"))
        });
    }

    public Task SetStatusAsync(Guid jobId, string status)
        => _mux.GetDatabase().StringSetAsync(KeyStatus(jobId), status);

    public async Task<string?> GetStatusAsync(Guid jobId)
        => await _mux.GetDatabase().StringGetAsync(KeyStatus(jobId));

    public Task AddResultAsync(Guid jobId, string content, double ts)
    {
        var db = _mux.GetDatabase();
        var json = JsonSerializer.Serialize(new { content, timestamp = ts });
        return db.SortedSetAddAsync(KeyResults(jobId), json, ts);
    }

    public async Task<object> GetResultsAsync(Guid jobId)
    {
        var db = _mux.GetDatabase();
        var arr = await db.SortedSetRangeByRankAsync(KeyResults(jobId), 0, -1, Order.Ascending);
        return arr.Select(x => JsonSerializer.Deserialize<object>(x!)).ToArray();
    }

    static string KeyStatus(Guid id) => $"job:{id}:status";

    static string KeyMeta(Guid id) => $"job:{id}:meta";
    
    static string KeyResults(Guid id) => $"job:{id}:results";
}
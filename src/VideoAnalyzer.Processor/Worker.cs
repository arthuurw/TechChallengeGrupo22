using System.Drawing;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;
using VideoAnalyzer.Shared;
using ZXing;
using FFMpegCore;

public class Worker : BackgroundService
{
    private readonly IConnection _rabbit;
    private readonly IConnectionMultiplexer _redis;
    private readonly HubConnection _hub;

    public Worker(IConnection rabbit, IConnectionMultiplexer redis, HubConnection hub)
    {
        _rabbit = rabbit;
        _redis = redis;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _hub.StartAsync(stoppingToken);
        var channel = _rabbit.CreateModel();
        channel.QueueDeclare(queue: "videos", durable: false, exclusive: false, autoDelete: false);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.Received += async (model, ea) =>
        {
            var msg = JsonSerializer.Deserialize<VideoProcessingMessage>(ea.Body.ToArray());
            if (msg == null) return;
            var db = _redis.GetDatabase();
            await db.StringSetAsync($"status:{msg.VideoId}", "Processing");
            var results = await ProcessVideoAsync(msg.FilePath, stoppingToken);
            var json = JsonSerializer.Serialize(results);
            await db.StringSetAsync($"results:{msg.VideoId}", json);
            await db.StringSetAsync($"status:{msg.VideoId}", "Completed");
            await _hub.SendAsync("Completed", msg.VideoId);
        };
        channel.BasicConsume(queue: "videos", autoAck: true, consumer: consumer);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task<List<QrCodeResult>> ProcessVideoAsync(string path, CancellationToken token)
    {
        var results = new List<QrCodeResult>();
        var info = await FFProbe.AnalyseAsync(path, token);
        var fps = info.PrimaryVideoStream?.AverageFrameRate ?? 30;
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(temp);
        await FFMpegArguments.FromFileInput(path)
            .OutputToFile(Path.Combine(temp, "frame_%05d.png"), true, options => options.WithVideoCodec("png"))
            .ProcessAsynchronously(token);
        var files = Directory.GetFiles(temp, "frame_*.png");
        var query = files.AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount)
            .Select(file =>
            {
                var index = int.Parse(Path.GetFileNameWithoutExtension(file).Split('_')[1]);
                using var bmp = (Bitmap)Image.FromFile(file);
                var reader = new BarcodeReader();
                var res = reader.Decode(bmp);
                if (res != null)
                {
                    return new QrCodeResult(index / fps, res.Text);
                }
                return null;
            })
            .Where(r => r != null)!
            .ToList();
        results.AddRange(query!);
        Directory.Delete(temp, true);
        return results;
    }
}

using StackExchange.Redis;
using VisionaryAnalytics.Api;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Rabbit;
using VisionaryAnalytics.Infrastructure.Redis;

var builder = WebApplication.CreateBuilder(args);

// Config
var cfg = builder.Configuration;
var uploadPath = cfg["UPLOAD_PATH"] ?? "/data/videos";
Directory.CreateDirectory(uploadPath);

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(cfg["REDIS__CONNECTION"] ?? "redis:6379"));
builder.Services.AddSingleton<IVideoJobStore, RedisVideoJobStore>();

// RabbitMQ Publisher
builder.Services.AddSingleton<IRabbitMqPublisher>(sp =>
    new RabbitMqPublisher(new RabbitMqOptions
    {
        HostName = cfg["RABBITMQ__HOST"] ?? "rabbitmq",
        UserName = cfg["RABBITMQ__USER"] ?? "guest",
        Password = cfg["RABBITMQ__PASSWORD"] ?? "guest",
        QueueName = cfg["RABBITMQ__QUEUE"] ?? "video-jobs"
    }));

// SignalR
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");

// Hub para notificações
app.MapHub<ProcessingHub>("/hubs/processing");

// Upload de vídeo
app.MapPost("/videos", async (HttpRequest req, IRabbitMqPublisher bus, IVideoJobStore store) =>
{
    if (!req.HasFormContentType) return Results.BadRequest("Form-data required");

    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest("Arquivo ausente");

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (ext is not (".mp4" or ".avi")) return Results.BadRequest("Formatos suportados: .mp4, .avi");

    var jobId = Guid.NewGuid();
    var dest = Path.Combine(uploadPath, $"{jobId}{ext}");
    await using (var fs = File.Create(dest))
        await file.CopyToAsync(fs);

    var fps = double.TryParse(form["fps"], out var f) ? Math.Max(1, f) : 5.0;

    await store.InitAsync(jobId, file.FileName, fps);
    await bus.PublishAsync(new VideoJobMessage(jobId, dest, fps));

    return Results.Ok(new
    {
        jobId,
        statusEndpoint = $"/videos/{jobId}/status",
        resultsEndpoint = $"/videos/{jobId}/results",
        signalRHub = "/hubs/processing"
    });
});

// Status
app.MapGet("/videos/{jobId:guid}/status", async (Guid jobId, IVideoJobStore store) =>
{
    var status = await store.GetStatusAsync(jobId);
    return status is null
        ? Results.NotFound()
        : Results.Ok(new { jobId, status });
});

// Resultados
app.MapGet("/videos/{jobId:guid}/results", async (Guid jobId, IVideoJobStore store) =>
{
    var status = await store.GetStatusAsync(jobId);
    if (status is null) return Results.NotFound();

    var results = await store.GetResultsAsync(jobId);
    return Results.Ok(new
    {
        jobId,
        status,
        results
    });
});

app.Run();

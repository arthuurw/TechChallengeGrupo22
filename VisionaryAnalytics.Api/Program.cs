using StackExchange.Redis;
using VisionaryAnalytics.Api;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Messaging;
using VisionaryAnalytics.Infrastructure.Models;
using VisionaryAnalytics.Infrastructure.Rabbit;
using VisionaryAnalytics.Infrastructure.Redis;

var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;
var uploadPath = configuration["UPLOAD_PATH"] ?? "/data/videos";
Directory.CreateDirectory(uploadPath);

builder.Services.Configure<RabbitMqOptions>(configuration.GetSection("RabbitMq"));
builder.Services.PostConfigure<RabbitMqOptions>(options =>
{
    options.HostName = string.IsNullOrWhiteSpace(options.HostName)
        ? configuration["RABBITMQ__HOST"] ?? options.HostName
        : options.HostName;
    options.UserName = string.IsNullOrWhiteSpace(options.UserName)
        ? configuration["RABBITMQ__USER"] ?? options.UserName
        : options.UserName;
    options.Password = string.IsNullOrWhiteSpace(options.Password)
        ? configuration["RABBITMQ__PASSWORD"] ?? options.Password
        : options.Password;
    options.QueueName = string.IsNullOrWhiteSpace(options.QueueName)
        ? configuration["RABBITMQ__QUEUE"] ?? options.QueueName
        : options.QueueName;
});

builder.Services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        configuration.GetConnectionString("Redis") ??
        configuration["Redis:Connection"] ??
        configuration["REDIS__CONNECTION"] ??
        "redis:6379"));

builder.Services.AddSingleton<IVideoJobStore, RedisVideoJobStore>();

builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");
app.MapHub<ProcessingHub>("/hubs/processing");

app.MapPost("/videos", async (HttpRequest request, IRabbitMqPublisher bus, IVideoJobStore store, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest("Form-data required");
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest("Arquivo ausente");
    }

    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (extension is not (".mp4" or ".avi"))
    {
        return Results.BadRequest("Formatos suportados: .mp4, .avi");
    }

    var jobId = Guid.NewGuid();
    var destination = Path.Combine(uploadPath, $"{jobId}{extension}");

    await using (var fileStream = File.Create(destination))
    {
        await file.CopyToAsync(fileStream, cancellationToken);
    }

    var fps = double.TryParse(form["fps"], out var parsedFps) ? Math.Max(1, parsedFps) : 5d;

    await store.InitAsync(jobId, file.FileName, fps, cancellationToken);
    await bus.PublishAsync(new VideoJobMessage(jobId, destination, fps), cancellationToken);

    return Results.Ok(new
    {
        jobId,
        status = VideoJobStatuses.Queued,
        statusEndpoint = $"/videos/{jobId}/status",
        resultsEndpoint = $"/videos/{jobId}/results",
        signalRHub = "/hubs/processing"
    });
});

app.MapGet("/videos/{jobId:guid}/status", async (Guid jobId, IVideoJobStore store, CancellationToken cancellationToken) =>
{
    var state = await store.GetStatusAsync(jobId, cancellationToken);
    if (state is null)
    {
        return Results.NotFound();
    }

    var metadata = await store.GetMetadataAsync(jobId, cancellationToken);

    return Results.Ok(new
    {
        jobId,
        status = state.Status,
        errorMessage = state.ErrorMessage,
        metadata
    });
});

app.MapGet("/videos/{jobId:guid}/results", async (Guid jobId, IVideoJobStore store, CancellationToken cancellationToken) =>
{
    var statusTask = store.GetStatusAsync(jobId, cancellationToken);
    var metadataTask = store.GetMetadataAsync(jobId, cancellationToken);
    var resultsTask = store.GetResultsAsync(jobId, cancellationToken);

    await Task.WhenAll(statusTask, metadataTask, resultsTask);

    var state = await statusTask;
    if (state is null)
    {
        return Results.NotFound();
    }

    var metadata = await metadataTask;
    var results = await resultsTask;

    return Results.Ok(new
    {
        jobId,
        status = state.Status,
        errorMessage = state.ErrorMessage,
        metadata,
        results
    });
});

app.Run();

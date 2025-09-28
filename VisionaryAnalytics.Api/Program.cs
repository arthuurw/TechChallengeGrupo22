using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using VisionaryAnalytics.Api;
using VisionaryAnalytics.Api.Options;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Rabbit;
using VisionaryAnalytics.Infrastructure.Redis;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services
    .AddOptions<RabbitMqOptions>()
    .BindConfiguration("RabbitMq")
    .Configure(opt =>
    {
        opt.HostName = cfg["RABBITMQ__HOST"] ?? opt.HostName;
        opt.UserName = cfg["RABBITMQ__USER"] ?? opt.UserName;
        opt.Password = cfg["RABBITMQ__PASSWORD"] ?? opt.Password;
        opt.QueueName = cfg["RABBITMQ__QUEUE"] ?? opt.QueueName;
    })
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<UploadOptions>()
    .BindConfiguration("Upload")
    .Configure(options =>
    {
        options.RootPath = cfg["UPLOAD__ROOT_PATH"] ?? cfg["UPLOAD_PATH"] ?? options.RootPath;
        if (long.TryParse(cfg["UPLOAD__MAX_FILE_SIZE_BYTES"] ?? cfg["UPLOAD__MAX_BYTES"], out var maxBytes) && maxBytes > 0)
        {
            options.MaxFileSizeBytes = maxBytes;
        }

        var allowedExtensions = cfg["UPLOAD__ALLOWED_EXTENSIONS"];
        if (!string.IsNullOrWhiteSpace(allowedExtensions))
        {
            options.AllowedExtensions = allowedExtensions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
    })
    .PostConfigure(options => options.Normalize())
    .ValidateDataAnnotations()
    .Validate(options => options.MaxFileSizeBytes > 0, "O tamanho máximo do arquivo deve ser maior que zero.")
    .ValidateOnStart();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisConfiguration = cfg["REDIS__CONNECTION"] ?? "redis:6379";
    var options = ConfigurationOptions.Parse(redisConfiguration);
    options.AbortOnConnectFail = false;
    options.ConnectRetry = 3;
    options.ClientName = "VisionaryAnalytics.Api";
    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddSingleton<IVideoJobStore, RedisVideoJobStore>();
builder.Services.AddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();

builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();

var uploadOptions = app.Services.GetRequiredService<IOptions<UploadOptions>>().Value;
Directory.CreateDirectory(uploadOptions.RootPath);

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();

app.MapHealthChecks("/health");

app.MapHub<ProcessingHub>("/hubs/processamento");

app.MapPost("/videos", async (HttpRequest req, IRabbitMqPublisher bus, IVideoJobStore store, IOptions<UploadOptions> uploadOptionsAccessor, CancellationToken cancellationToken) =>
{
    if (!req.HasFormContentType)
    {
        return Results.BadRequest("É necessário enviar o conteúdo como form-data.");
    }

    var form = await req.ReadFormAsync(cancellationToken).ConfigureAwait(false);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest("Arquivo ausente");
    }

    var options = uploadOptionsAccessor.Value;
    if (file.Length > options.MaxFileSizeBytes)
    {
        return Results.BadRequest($"Arquivo excede o tamanho máximo permitido de {options.MaxFileSizeBytes} bytes.");
    }

    var ext = Path.GetExtension(file.FileName);
    if (string.IsNullOrWhiteSpace(ext))
    {
        return Results.BadRequest("Formato de arquivo inválido.");
    }

    var normalizedExt = ext.ToLowerInvariant();
    if (!options.AllowedExtensionsSet.Contains(normalizedExt))
    {
        var allowed = string.Join(", ", options.AllowedExtensionsSet.OrderBy(static e => e, StringComparer.OrdinalIgnoreCase));
        return Results.BadRequest($"Formatos suportados: {allowed}");
    }

    var jobId = Guid.NewGuid();
    var destinationPath = Path.Combine(options.RootPath, $"{jobId}{normalizedExt}");

    await using (var fs = new FileStream(destinationPath, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
    }))
    {
        await file.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
    }

    var fpsValue = form["fps"].ToString();
    var fps = TryParseFps(fpsValue, out var parsedFps) ? parsedFps : 5.0d;

    var safeFileName = Path.GetFileName(file.FileName);
    var correlationId = req.Headers.TryGetValue("X-Correlation-ID", out var correlationHeader)
        ? correlationHeader.ToString()
        : req.HttpContext.TraceIdentifier;

    await store.InitAsync(jobId, safeFileName, fps).ConfigureAwait(false);
    await bus.PublishAsync(new VideoJobMessage(jobId, destinationPath, fps, correlationId), cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        jobId,
        statusEndpoint = $"/videos/{jobId}/status",
        resultsEndpoint = $"/videos/{jobId}/results",
        signalRHub = "/hubs/processamento"
    });
});

app.MapGet("/videos/{jobId:guid}/status", async (Guid jobId, IVideoJobStore store) =>
{
    var status = await store.GetStatusAsync(jobId).ConfigureAwait(false);
    return status is null
        ? Results.NotFound()
        : Results.Ok(new { jobId, status });
});

app.MapGet("/videos/{jobId:guid}/results", async (Guid jobId, IVideoJobStore store) =>
{
    var status = await store.GetStatusAsync(jobId).ConfigureAwait(false);
    if (status is null)
    {
        return Results.NotFound();
    }

    var results = await store.GetResultsAsync(jobId).ConfigureAwait(false);
    return Results.Ok(new
    {
        jobId,
        status,
        results
    });
});

app.Run();

static bool TryParseFps(string? value, out double fps)
{
    if (!string.IsNullOrWhiteSpace(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
    {
        fps = Math.Clamp(parsed, 1d, 120d);
        return true;
    }

    fps = 0;
    return false;
}

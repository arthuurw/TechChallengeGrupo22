using System.Diagnostics.CodeAnalysis;
using FFMpegCore;
using StackExchange.Redis;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Rabbit;
using VisionaryAnalytics.Infrastructure.Redis;
using VisionaryAnalytics.Worker;
using VisionaryAnalytics.Worker.Notifications;
using VisionaryAnalytics.Worker.Options;
using VisionaryAnalytics.Worker.Processing;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;

GlobalFFOptions.Configure(options =>
{
    var binaryFolder = configuration["FFMPEG__BINARY_FOLDER"];
    if (!string.IsNullOrWhiteSpace(binaryFolder))
    {
        options.BinaryFolder = binaryFolder;
    }

    var tempDirectory = configuration["VideoProcessing:TempDirectory"];
    if (!string.IsNullOrWhiteSpace(tempDirectory))
    {
        options.TemporaryFilesFolder = tempDirectory;
    }
});

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

builder.Services.Configure<VideoProcessingOptions>(configuration.GetSection("VideoProcessing"));
builder.Services.Configure<SignalROptions>(configuration.GetSection("SignalR"));

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        configuration.GetConnectionString("Redis") ??
        configuration["Redis:Connection"] ??
        configuration["REDIS__CONNECTION"] ??
        "redis:6379"));

builder.Services.AddSingleton<IVideoJobStore, RedisVideoJobStore>();
builder.Services.AddSingleton<IHubConnectionFactory, HubConnectionFactory>();
builder.Services.AddSingleton<IProcessingNotifier, SignalRProcessingNotifier>();
builder.Services.AddSingleton<IVideoJobProcessor, VideoJobProcessor>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

[ExcludeFromCodeCoverage]
public partial class Program;

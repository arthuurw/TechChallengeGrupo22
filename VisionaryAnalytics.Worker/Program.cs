using StackExchange.Redis;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Rabbit;
using VisionaryAnalytics.Infrastructure.Redis;
using VisionaryAnalytics.Worker;
using VisionaryAnalytics.Worker.Notifications;
using VisionaryAnalytics.Worker.Processing;

var builder = Host.CreateApplicationBuilder(args);
var configuration = builder.Configuration;

builder.Services
    .AddOptions<RabbitMqOptions>()
    .BindConfiguration("RabbitMq")
    .Configure(opt =>
    {
        opt.HostName = configuration["RABBITMQ__HOST"] ?? opt.HostName;
        opt.UserName = configuration["RABBITMQ__USER"] ?? opt.UserName;
        opt.Password = configuration["RABBITMQ__PASSWORD"] ?? opt.Password;
        opt.QueueName = configuration["RABBITMQ__QUEUE"] ?? opt.QueueName;
    });

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(configuration["REDIS__CONNECTION"] ?? "redis:6379"));

builder.Services.AddSingleton<IVideoJobStore, RedisVideoJobStore>();
builder.Services.AddSingleton<VideoProcessingService>();
builder.Services.AddSingleton<IProcessingNotifier, SignalRProcessingNotifier>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using StackExchange.Redis;
using Microsoft.AspNetCore.SignalR.Client;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.AddSingleton<ConnectionFactory>(_ => new ConnectionFactory{ HostName = ctx.Configuration["RabbitMQ:Host"] ?? "rabbitmq" });
        services.AddSingleton<IConnection>(sp => sp.GetRequiredService<ConnectionFactory>().CreateConnection());
        services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(ctx.Configuration["Redis:Host"] ?? "redis"));
        services.AddSingleton(sp => new HubConnectionBuilder().WithUrl(ctx.Configuration["SignalR:Hub"] ?? "http://api/hubs/processing").WithAutomaticReconnect().Build());
        services.AddHostedService<Worker>();
    })
    .Build();

await host.RunAsync();

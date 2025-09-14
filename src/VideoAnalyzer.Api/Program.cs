using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using StackExchange.Redis;
using System.Text;
using System.Text.Json;
using VideoAnalyzer.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ConnectionFactory>(_ => new ConnectionFactory{ HostName = builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq" });
builder.Services.AddSingleton<IConnection>(sp => sp.GetRequiredService<ConnectionFactory>().CreateConnection());
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(builder.Configuration["Redis:Host"] ?? "redis"));
builder.Services.AddSignalR();

var app = builder.Build();

app.MapHub<ProcessingHub>("/hubs/processing");

app.MapPost("/videos", async (HttpRequest request, IConnection rabbit, IConnectionMultiplexer redis) =>
{
    if (!request.HasFormContentType) return Results.BadRequest("Expected form content");
    var form = await request.ReadFormAsync();
    if (form.Files.Count == 0) return Results.BadRequest("File missing");
    var file = form.Files[0];
    var id = Guid.NewGuid();
    var uploads = Path.Combine(app.Environment.ContentRootPath, "uploads");
    Directory.CreateDirectory(uploads);
    var filePath = Path.Combine(uploads, id + Path.GetExtension(file.FileName));
    await using (var stream = File.Create(filePath))
    {
        await file.CopyToAsync(stream);
    }

    var db = redis.GetDatabase();
    await db.StringSetAsync($"status:{id}", "Queued");

    var message = new VideoProcessingMessage(id, filePath);
    var channel = rabbit.CreateModel();
    channel.QueueDeclare(queue: "videos", durable: false, exclusive: false, autoDelete: false);
    var body = JsonSerializer.SerializeToUtf8Bytes(message);
    channel.BasicPublish(exchange: "", routingKey: "videos", basicProperties: null, body: body);

    return Results.Ok(new { id });
});

app.MapGet("/videos/{id}/status", async (Guid id, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var status = await db.StringGetAsync($"status:{id}");
    if (status.IsNullOrEmpty) return Results.NotFound();
    return Results.Ok(status.ToString());
});

app.MapGet("/videos/{id}/results", async (Guid id, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var json = await db.StringGetAsync($"results:{id}");
    if (json.IsNullOrEmpty) return Results.NotFound();
    var results = JsonSerializer.Deserialize<List<QrCodeResult>>(json!);
    return Results.Ok(results);
});

app.Run();

class ProcessingHub : Microsoft.AspNetCore.SignalR.Hub { }

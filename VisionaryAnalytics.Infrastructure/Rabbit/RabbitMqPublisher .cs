using Microsoft.EntityFrameworkCore.Metadata;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using VisionaryAnalytics.Infrastructure.Interface;

namespace VisionaryAnalytics.Infrastructure.Rabbit;

public class RabbitMqPublisher : IRabbitMqPublisher, IAsyncDisposable
{
    private readonly IConnection _conn;
    private readonly IModel _ch;
    private readonly RabbitMqOptions _opt;

    public RabbitMqPublisher(RabbitMqOptions opt)
    {
        _opt = opt;
        var factory = new ConnectionFactory
        {
            HostName = opt.HostName,
            UserName = opt.UserName,
            Password = opt.Password
        };
        _conn = factory.CreateConnection();
        _ch = _conn.CreateModel();
        _ch.QueueDeclare(
            queue: opt.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null
        );
    }

    public Task PublishAsync<T>(T message)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var props = _ch.CreateBasicProperties();
        props.DeliveryMode = 2;
        _ch.BasicPublish("", _opt.QueueName, props, body);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _ch?.Dispose();
        _conn?.Dispose();
        return ValueTask.CompletedTask;
    }
}
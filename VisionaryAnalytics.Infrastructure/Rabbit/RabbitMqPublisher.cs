using RabbitMQ.Client;
using System.Text.Json;
using Microsoft.Extensions.Options;
using VisionaryAnalytics.Infrastructure.Interface;

namespace VisionaryAnalytics.Infrastructure.Rabbit;

public sealed class RabbitMqPublisher : IRabbitMqPublisher
{
    private readonly IConnection _connection;
    private readonly RabbitMqOptions _options;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public RabbitMqPublisher(IOptions<RabbitMqOptions> optionsAccessor)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        _options = optionsAccessor.Value;

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();

        using var channel = _connection.CreateModel();
        channel.QueueDeclare(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);
    }

    public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var channel = _connection.CreateModel();
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.DeliveryMode = 2;

        channel.BasicPublish(
            exchange: string.Empty,
            routingKey: _options.QueueName,
            basicProperties: properties,
            body: payload);

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }
}

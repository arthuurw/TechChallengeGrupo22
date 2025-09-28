using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using VisionaryAnalytics.Infrastructure.Interface;

namespace VisionaryAnalytics.Infrastructure.Rabbit;

public sealed class RabbitMqPublisher : IRabbitMqPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly Lazy<IConnection> _connectionFactory;
    private readonly Lazy<IModel> _channelFactory;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private readonly RabbitMqOptions _options;

    public RabbitMqPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqPublisher> logger)
    {
        _logger = logger;
        _options = options.Value;
        _connectionFactory = new Lazy<IConnection>(CreateConnection, LazyThreadSafetyMode.ExecutionAndPublication);
        _channelFactory = new Lazy<IModel>(CreateChannel, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var channel = _channelFactory.Value;
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        var props = channel.CreateBasicProperties();
        props.DeliveryMode = 2;
        props.ContentType = "application/json";

        channel.BasicPublish(
            exchange: string.Empty,
            routingKey: _options.QueueName,
            mandatory: false,
            basicProperties: props,
            body: payload);

        _logger.LogDebug("Mensagem publicada na fila {QueueName}", _options.QueueName);
        return Task.CompletedTask;
    }

    private IConnection CreateConnection()
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = false,
            AutomaticRecoveryEnabled = true
        };

        _logger.LogInformation("Estabelecendo conexão RabbitMQ com {Host}", _options.HostName);
        return factory.CreateConnection("VisionaryAnalytics.Publisher");
    }

    private IModel CreateChannel()
    {
        var channel = _connectionFactory.Value.CreateModel();
        channel.QueueDeclare(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        return channel;
    }

    public ValueTask DisposeAsync()
    {
        if (_channelFactory.IsValueCreated)
        {
            var channel = _channelFactory.Value;
            try
            {
                channel.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao encerrar o canal RabbitMQ corretamente");
            }
            finally
            {
                channel.Dispose();
            }
        }

        if (_connectionFactory.IsValueCreated)
        {
            var connection = _connectionFactory.Value;
            try
            {
                connection.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao encerrar a conexão RabbitMQ corretamente");
            }
            finally
            {
                connection.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }
}

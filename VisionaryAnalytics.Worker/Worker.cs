using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VisionaryAnalytics.Infrastructure.Messaging;
using VisionaryAnalytics.Infrastructure.Rabbit;
using VisionaryAnalytics.Worker.Processing;

namespace VisionaryAnalytics.Worker;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private IConnection? _connection;
    private IModel? _channel;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    public Worker(ILogger<Worker> logger, IServiceScopeFactory scopeFactory, IOptions<RabbitMqOptions> options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.QueueDeclare(_options.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null);
        _channel.BasicQos(0, _options.PrefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += (sender, args) => HandleMessageAsync(args, stoppingToken);
        _channel.BasicConsume(queue: _options.QueueName, autoAck: false, consumer: consumer);

        _logger.LogInformation("Worker iniciado e aguardando mensagens na fila {Queue}", _options.QueueName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Expected on shutdown
        }
    }

    private async Task HandleMessageAsync(BasicDeliverEventArgs eventArgs, CancellationToken stoppingToken)
    {
        VideoJobMessage? message = null;
        try
        {
            message = JsonSerializer.Deserialize<VideoJobMessage>(eventArgs.Body.Span, _serializerOptions);
            if (message is null)
            {
                _logger.LogWarning("Mensagem inválida recebida. DeliveryTag: {DeliveryTag}", eventArgs.DeliveryTag);
                _channel?.BasicAck(eventArgs.DeliveryTag, multiple: false);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IVideoJobProcessor>();
            await processor.ProcessAsync(message, stoppingToken);

            _channel?.BasicAck(eventArgs.DeliveryTag, multiple: false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Cancelando processamento da mensagem {DeliveryTag}", eventArgs.DeliveryTag);
            _channel?.BasicNack(eventArgs.DeliveryTag, multiple: false, requeue: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar mensagem {DeliveryTag} para o job {JobId}", eventArgs.DeliveryTag, message?.JobId);
            _channel?.BasicAck(eventArgs.DeliveryTag, multiple: false);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _channel?.Close();
        _connection?.Close();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}

using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Rabbit;
using VisionaryAnalytics.Infrastructure.Redis;
using VisionaryAnalytics.Worker.Notifications;
using VisionaryAnalytics.Worker.Processing;

namespace VisionaryAnalytics.Worker;

public class Worker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private IConnection? _connection;
    private IModel? _channel;

    public Worker(
        ILogger<Worker> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<RabbitMqOptions> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ClientProvidedName = "VisionaryAnalytics.Worker"
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();
        _channel.QueueDeclare(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);
        _channel.BasicQos(0, 1, false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) =>
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IVideoJobStore>();
            var processor = scope.ServiceProvider.GetRequiredService<VideoProcessingService>();
            var notifier = scope.ServiceProvider.GetRequiredService<IProcessingNotifier>();

            VideoJobMessage? job = null;
            try
            {
                var payload = Encoding.UTF8.GetString(ea.Body.Span);
                job = JsonSerializer.Deserialize<VideoJobMessage>(payload, SerializerOptions);
                if (job is null)
                {
                    _logger.LogWarning("Mensagem inválida recebida; descartando.");
                    _channel!.BasicAck(ea.DeliveryTag, false);
                    return;
                }

                _logger.LogInformation("Processando job {JobId}", job.JobId);
                await store.SetStatusAsync(job.JobId, "Processando").ConfigureAwait(false);

                if (!File.Exists(job.FilePath))
                {
                    _logger.LogWarning("Arquivo de origem {FilePath} do job {JobId} não foi encontrado.", job.FilePath, job.JobId);
                    await store.SetStatusAsync(job.JobId, "Falha:ArquivoAusente").ConfigureAwait(false);
                    _channel!.BasicAck(ea.DeliveryTag, false);
                    return;
                }

                var results = await processor.AnalyzeAsync(job, stoppingToken).ConfigureAwait(false);
                foreach (var result in results)
                {
                    await store.AddResultAsync(job.JobId, result.Content, result.TimestampSeconds).ConfigureAwait(false);
                }

                await store.SetStatusAsync(job.JobId, "Concluido").ConfigureAwait(false);
                await notifier.NotificarConclusaoAsync(job.JobId, results.Count, stoppingToken).ConfigureAwait(false);

                _channel!.BasicAck(ea.DeliveryTag, false);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Falha ao desserializar mensagem com delivery tag {DeliveryTag}.", ea.DeliveryTag);
                _channel!.BasicAck(ea.DeliveryTag, false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Cancelamento solicitado. Mensagem retornará para a fila.");
                _channel!.BasicNack(ea.DeliveryTag, false, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao processar o job {JobId}", job?.JobId);
                if (job is not null)
                {
                    await store.SetStatusAsync(job.JobId, "Falha").ConfigureAwait(false);
                }

                _channel!.BasicNack(ea.DeliveryTag, false, false);
            }
        };

        _channel.BasicConsume(queue: _options.QueueName, autoAck: false, consumer: consumer);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stoppingToken.Register(() => completion.TrySetResult());
        return completion.Task;
    }

    public override void Dispose()
    {
        base.Dispose();
        _channel?.Close();
        _channel?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }
}

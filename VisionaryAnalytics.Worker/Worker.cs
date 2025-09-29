using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using VisionaryAnalytics.Infrastructure.Messaging;
using VisionaryAnalytics.Infrastructure.Rabbit;
using VisionaryAnalytics.Worker.Processing;

namespace VisionaryAnalytics.Worker;

[ExcludeFromCodeCoverage]
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RabbitMqOptions _options;
    private IConnection? _connection;
    private IModel? _channel;
    private AsyncEventingBasicConsumer? _consumer;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private TaskCompletionSource<ConsumerRestartRequest?>? _consumerRestartSignal;
    private CancellationToken _stoppingToken;

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
        _stoppingToken = stoppingToken;

        while (!stoppingToken.IsCancellationRequested)
        {
            var restartSignal = new TaskCompletionSource<ConsumerRestartRequest?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _consumerRestartSignal = restartSignal;

            try
            {
                InitializeConsumer();

                var waitForShutdown = Task.Delay(Timeout.Infinite, stoppingToken);
                var completedTask = await Task.WhenAny(restartSignal.Task, waitForShutdown);

                if (completedTask == waitForShutdown)
                {
                    await waitForShutdown;
                }
                else if (!stoppingToken.IsCancellationRequested)
                {
                    var restartRequest = await restartSignal.Task;

                    if (restartRequest is not null)
                    {
                        if (restartRequest.Exception is not null)
                        {
                            _logger.LogWarning(restartRequest.Exception, "Canal do RabbitMQ encerrado por {Source}. Detalhes: {Detail}", restartRequest.Source, restartRequest.Detail ?? "sem detalhes");
                        }
                        else
                        {
                            _logger.LogWarning("Canal do RabbitMQ encerrado por {Source}. Detalhes: {Detail}", restartRequest.Source, restartRequest.Detail ?? "sem detalhes");
                        }
                    }

                    CleanupConnection();
                    await Task.Delay(_reconnectDelay, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao inicializar o consumidor. Tentando reconectar em {Delay}s", _reconnectDelay.TotalSeconds);
                await Task.Delay(_reconnectDelay, stoppingToken);
            }
            finally
            {
                if (ReferenceEquals(_consumerRestartSignal, restartSignal))
                {
                    _consumerRestartSignal = null;
                }
            }
        }
    }

    private void InitializeConsumer()
    {
        if (_channel?.IsOpen == true && _connection?.IsOpen == true)
        {
            return;
        }

        CleanupConnection();

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            UserName = _options.UserName,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _connection.ConnectionShutdown += HandleConnectionShutdown;

        _channel = _connection.CreateModel();
        _channel.ModelShutdown += HandleChannelShutdown;
        _channel.CallbackException += HandleChannelCallbackException;
        _channel.QueueDeclare(_options.QueueName, durable: true, exclusive: false, autoDelete: false, arguments: null);

        var prefetchCount = _options.PrefetchCount > 0 ? _options.PrefetchCount : (ushort)1;
        _channel.BasicQos(0, prefetchCount, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += HandleMessageAsyncWrapper;
        consumer.Shutdown += HandleConsumerShutdownAsync;
        _consumer = consumer;
        _channel.BasicConsume(queue: _options.QueueName, autoAck: false, consumer: consumer);

        _logger.LogInformation("Worker iniciado e aguardando mensagens na fila {Queue}", _options.QueueName);
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

            if (_channel?.IsOpen == true)
            {
                _channel.BasicAck(eventArgs.DeliveryTag, multiple: false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Cancelando processamento da mensagem {DeliveryTag}", eventArgs.DeliveryTag);
            if (_channel?.IsOpen == true)
            {
                _channel.BasicNack(eventArgs.DeliveryTag, multiple: false, requeue: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar mensagem {DeliveryTag} para o job {JobId}", eventArgs.DeliveryTag, message?.JobId);
            if (_channel?.IsOpen == true)
            {
                _channel.BasicNack(eventArgs.DeliveryTag, multiple: false, requeue: true);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        CleanupConnection();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        CleanupConnection();
        base.Dispose();
    }

    private void CleanupConnection()
    {
        if (_consumer is not null)
        {
            _consumer.Received -= HandleMessageAsyncWrapper;
            _consumer.Shutdown -= HandleConsumerShutdownAsync;
            _consumer = null;
        }

        if (_channel is not null)
        {
            _channel.ModelShutdown -= HandleChannelShutdown;
            _channel.CallbackException -= HandleChannelCallbackException;
        }

        if (_connection is not null)
        {
            _connection.ConnectionShutdown -= HandleConnectionShutdown;
        }

        try
        {
            if (_channel?.IsOpen == true)
            {
                _channel.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao fechar o canal do RabbitMQ");
        }
        finally
        {
            _channel?.Dispose();
            _channel = null;
        }

        try
        {
            if (_connection?.IsOpen == true)
            {
                _connection.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao fechar a conexão do RabbitMQ");
        }
        finally
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    private void HandleConnectionShutdown(object? sender, ShutdownEventArgs args)
    {
        RequestConsumerRestart("Connection", args.ReplyText, null);
    }

    private void HandleChannelShutdown(object? sender, ShutdownEventArgs args)
    {
        RequestConsumerRestart("Channel", args.ReplyText, null);
    }

    private void HandleChannelCallbackException(object? sender, CallbackExceptionEventArgs args)
    {
        var detail = args.Detail ?? args.Exception?.Message;
        RequestConsumerRestart("Callback", detail, args.Exception);
    }

    private Task HandleConsumerShutdownAsync(object sender, ShutdownEventArgs args)
    {
        RequestConsumerRestart("Consumer", args.ReplyText, null);
        return Task.CompletedTask;
    }

    private Task HandleMessageAsyncWrapper(object sender, BasicDeliverEventArgs eventArgs)
        => HandleMessageAsync(eventArgs, _stoppingToken);

    private void RequestConsumerRestart(string source, string? detail, Exception? exception)
    {
        if (_stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var restartSignal = _consumerRestartSignal;
        restartSignal?.TrySetResult(new ConsumerRestartRequest(source, detail, exception));
    }

    private sealed record ConsumerRestartRequest(string Source, string? Detail, Exception? Exception);
}

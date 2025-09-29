using FluentAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using VisionaryAnalytics.Worker.Notifications;
using VisionaryAnalytics.Worker.Options;

namespace VisionaryAnalytics.Tests.Unit;

public class TestesSignalRProcessingNotifier
{
    private static SignalRProcessingNotifier CriarSut(
        SignalROptions? options = null,
        IHubConnectionFactory? factory = null,
        Mock<ILogger<SignalRProcessingNotifier>>? loggerMock = null)
    {
        options ??= new SignalROptions
        {
            EnableNotifications = true,
            HubUrl = "http://localhost/hub"
        };

        factory ??= new HubConnectionFactoryFalsa();
        loggerMock ??= new Mock<ILogger<SignalRProcessingNotifier>>();

        return new SignalRProcessingNotifier(
            Options.Create(options),
            loggerMock.Object,
            factory);
    }

    [Fact]
    public async Task NotifyCompleted_DeveIgnorarQuandoDesabilitado()
    {
        var factory = new HubConnectionFactoryFalsa();
        var notifier = CriarSut(new SignalROptions { EnableNotifications = false }, factory);

        await notifier.NotifyCompletedAsync(Guid.NewGuid(), 5);

        factory.Criacoes.Should().Be(0);
    }

    [Fact]
    public async Task NotifyCompleted_DeveIgnorarQuandoUrlVazia()
    {
        var factory = new HubConnectionFactoryFalsa();
        var notifier = CriarSut(new SignalROptions { EnableNotifications = true, HubUrl = string.Empty }, factory);

        await notifier.NotifyCompletedAsync(Guid.NewGuid(), 5);

        factory.Criacoes.Should().Be(0);
    }

    [Fact]
    public async Task NotifyCompleted_DeveInvocarHubQuandoConexaoObtida()
    {
        var conexao = new HubConnectionContextFalso();
        var factory = new HubConnectionFactoryFalsa(conexao);
        var notifier = CriarSut(factory: factory);
        var jobId = Guid.NewGuid();

        await notifier.NotifyCompletedAsync(jobId, 2);

        conexao.Invocacoes.Should().ContainSingle(invocacao =>
            invocacao.nomeMetodo == "NotifyCompleted" &&
            invocacao.argumentos[0].Equals(jobId) &&
            invocacao.argumentos[1].Equals(2));
    }

    [Fact]
    public async Task NotifyFailed_DeveTratarExcecoesComoAviso()
    {
        var conexao = new HubConnectionContextFalso
        {
            DeveFalharAoInvocar = true
        };
        var factory = new HubConnectionFactoryFalsa(conexao);
        var loggerMock = new Mock<ILogger<SignalRProcessingNotifier>>();
        loggerMock.Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Verifiable();

        var notifier = CriarSut(factory: factory, loggerMock: loggerMock);

        await notifier.NotifyFailedAsync(Guid.NewGuid(), "erro");

        loggerMock.Verify();
    }

    [Fact]
    public async Task NotifyCompleted_DeveRegistrarAvisoQuandoFalhaAoConectar()
    {
        var factory = new HubConnectionFactoryFalsa
        {
            DeveLancarAoCriar = true
        };
        var loggerMock = new Mock<ILogger<SignalRProcessingNotifier>>();
        loggerMock.Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Verifiable();

        var notifier = CriarSut(factory: factory, loggerMock: loggerMock);

        await notifier.NotifyCompletedAsync(Guid.NewGuid(), 1);

        loggerMock.Verify();
    }

    [Fact]
    public async Task DisposeAsync_DeveLiberarConexao()
    {
        var conexao = new HubConnectionContextFalso();
        var factory = new HubConnectionFactoryFalsa(conexao);
        var notifier = CriarSut(factory: factory);

        await notifier.NotifyCompletedAsync(Guid.NewGuid(), 1);
        await notifier.DisposeAsync();

        conexao.Disposed.Should().BeTrue();
    }

    private sealed class HubConnectionFactoryFalsa : IHubConnectionFactory
    {
        private readonly Queue<IHubConnectionContext> _conexoes;

        public HubConnectionFactoryFalsa(params IHubConnectionContext[] conexoes)
        {
            _conexoes = new Queue<IHubConnectionContext>(conexoes.Length == 0
                ? new[] { new HubConnectionContextFalso() }
                : conexoes);
        }

        public bool DeveLancarAoCriar { get; set; }

        public int Criacoes { get; private set; }

        public IHubConnectionContext Create(string hubUrl)
        {
            Criacoes++;
            if (DeveLancarAoCriar)
            {
                throw new InvalidOperationException("falha ao criar conexão");
            }

            return _conexoes.Dequeue();
        }
    }

    private sealed class HubConnectionContextFalso : IHubConnectionContext
    {
        public bool DeveFalharAoInvocar { get; set; }
        public bool Disposed { get; private set; }
        public List<(string nomeMetodo, object?[] argumentos)> Invocacoes { get; } = new();

        public HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            State = HubConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task InvokeAsync(string methodName, object? arg1, object? arg2, CancellationToken cancellationToken = default)
        {
            if (DeveFalharAoInvocar)
            {
                throw new InvalidOperationException("falha na invocação");
            }

            Invocacoes.Add((methodName, new[] { arg1, arg2 }));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            State = HubConnectionState.Disconnected;
            return ValueTask.CompletedTask;
        }
    }
}

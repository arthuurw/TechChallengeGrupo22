using FluentAssertions;
using Moq;
using StackExchange.Redis;
using VisionaryAnalytics.Infrastructure.Models;
using VisionaryAnalytics.Infrastructure.Redis;

namespace VisionaryAnalytics.Tests.Unit;

public class TestesRedisVideoJobStore
{
    private readonly Mock<IConnectionMultiplexer> _multiplexadorMock = new();
    private readonly Mock<IDatabase> _bancoMock = new();

    public TestesRedisVideoJobStore()
    {
        _multiplexadorMock.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object?>()))
            .Returns(_bancoMock.Object);

        _bancoMock.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _bancoMock.Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _bancoMock.Setup(db => db.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _bancoMock.Setup(db => db.SortedSetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
    }

    private RedisVideoJobStore CriarSut() => new(_multiplexadorMock.Object);

    [Fact]
    public async Task InitAsync_DeveInicializarChaves()
    {
        var armazenamento = CriarSut();
        var jobId = Guid.NewGuid();

        await armazenamento.InitAsync(jobId, "video.mp4", 10);

        _bancoMock.Verify(db => db.StringSetAsync(It.Is<RedisKey>(k => k == $"job:{jobId}:status"), VideoJobStatuses.Queued, null, When.Always, CommandFlags.None), Times.Once);
        _bancoMock.Verify(db => db.KeyDeleteAsync(It.Is<RedisKey>(k => k == $"job:{jobId}:error"), CommandFlags.None), Times.Once);
        _bancoMock.Verify(db => db.KeyDeleteAsync(It.Is<RedisKey>(k => k == $"job:{jobId}:results"), CommandFlags.None), Times.Once);
        _bancoMock.Verify(db => db.HashSetAsync(
            It.Is<RedisKey>(k => k == $"job:{jobId}:meta"),
            It.Is<HashEntry[]>(entries =>
                entries.Any(e => e.Name == "nomeArquivo" && e.Value == "video.mp4") &&
                entries.Any(e => e.Name == "fps" && e.Value == 10d.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)) &&
                entries.Any(e => e.Name == "criadoEm")),
            When.Always,
            CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public async Task SetStatusAsync_DeveArmazenarErroQuandoInformado()
    {
        var armazenamento = CriarSut();
        var jobId = Guid.NewGuid();

        await armazenamento.SetStatusAsync(jobId, VideoJobStatuses.Failed, "algo deu errado");

        _bancoMock.Verify(db => db.StringSetAsync(It.Is<RedisKey>(k => k == $"job:{jobId}:status"), VideoJobStatuses.Failed, null, When.Always, CommandFlags.None), Times.Once);
        _bancoMock.Verify(db => db.StringSetAsync(It.Is<RedisKey>(k => k == $"job:{jobId}:error"), "algo deu errado", null, When.Always, CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task SetStatusAsync_DeveLimparErroQuandoNulo()
    {
        var armazenamento = CriarSut();
        var jobId = Guid.NewGuid();

        await armazenamento.SetStatusAsync(jobId, VideoJobStatuses.Processing, null);

        _bancoMock.Verify(db => db.StringSetAsync(It.Is<RedisKey>(k => k == $"job:{jobId}:status"), VideoJobStatuses.Processing, null, When.Always, CommandFlags.None), Times.Once);
        _bancoMock.Verify(db => db.KeyDeleteAsync(It.Is<RedisKey>(k => k == $"job:{jobId}:error"), CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task GetStatusAsync_DeveRetornarNuloQuandoStatusAusente()
    {
        var armazenamento = CriarSut();
        var jobId = Guid.NewGuid();

        _bancoMock.Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.Null);

        var estado = await armazenamento.GetStatusAsync(jobId);

        estado.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_DeveRetornarEstado()
    {
        var armazenamento = CriarSut();
        var jobId = Guid.NewGuid();

        _bancoMock.SetupSequence(db => db.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(VideoJobStatuses.Completed)
            .ReturnsAsync("erro");

        var estado = await armazenamento.GetStatusAsync(jobId);

        estado.Should().NotBeNull();
        estado!.Status.Should().Be(VideoJobStatuses.Completed);
        estado.MensagemErro.Should().Be("erro");
    }

    [Fact]
    public async Task AddResultAsync_DeveSerializarEArmazenarResultado()
    {
        var armazenamento = CriarSut();
        var jobId = Guid.NewGuid();
        VideoJobResult? resultadoCapturado = null;

        _bancoMock.Setup(db => db.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Callback<RedisKey, RedisValue, double, When, CommandFlags>((_, value, _, _, _) =>
            {
                resultadoCapturado = System.Text.Json.JsonSerializer.Deserialize<VideoJobResult>(value!);
            })
            .ReturnsAsync(true);

        var resultadoEsperado = new VideoJobResult("quadro", 12.3);
        await armazenamento.AddResultAsync(jobId, resultadoEsperado);

        resultadoCapturado.Should().BeEquivalentTo(resultadoEsperado);
    }

    [Fact]
    public async Task GetResultsAsync_DeveRetornarListaVaziaQuandoSemResultados()
    {
        var armazenamento = CriarSut();

        _bancoMock.Setup(db => db.SortedSetRangeByRankAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<Order>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Array.Empty<RedisValue>());

        var resultados = await armazenamento.GetResultsAsync(Guid.NewGuid());

        resultados.Should().BeEmpty();
    }

    [Fact]
    public async Task GetResultsAsync_DeveDesserializarEntradas()
    {
        var armazenamento = CriarSut();
        var jobId = Guid.NewGuid();
        var cargaSerializada = System.Text.Json.JsonSerializer.Serialize(new VideoJobResult("quadro", 2.5));

        _bancoMock.Setup(db => db.SortedSetRangeByRankAsync(It.IsAny<RedisKey>(), 0, -1, Order.Ascending, CommandFlags.None))
            .ReturnsAsync(new RedisValue[] { cargaSerializada });

        var resultados = await armazenamento.GetResultsAsync(jobId);

        resultados.Should().ContainSingle();
        resultados[0].Conteudo.Should().Be("quadro");
        resultados[0].InstanteSegundos.Should().Be(2.5);
    }

    [Fact]
    public async Task GetMetadataAsync_DeveRetornarNuloQuandoNaoExistemEntradas()
    {
        var armazenamento = CriarSut();

        _bancoMock.Setup(db => db.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(Array.Empty<HashEntry>());

        var metadados = await armazenamento.GetMetadataAsync(Guid.NewGuid());

        metadados.Should().BeNull();
    }

    [Fact]
    public async Task GetMetadataAsync_DeveRetornarMetadataProcessada()
    {
        var armazenamento = CriarSut();
        var jobId = Guid.NewGuid();
        var criadoEm = DateTimeOffset.UtcNow;

        _bancoMock.Setup(db => db.HashGetAllAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(new[]
            {
                new HashEntry("nomeArquivo", "video.mp4"),
                new HashEntry("fps", "24"),
                new HashEntry("criadoEm", criadoEm.ToString("O"))
            });

        var metadados = await armazenamento.GetMetadataAsync(jobId);

        metadados.Should().NotBeNull();
        metadados!.NomeArquivo.Should().Be("video.mp4");
        metadados.QuadrosPorSegundo.Should().Be(24);
        metadados.CriadoEm.Should().Be(criadoEm);
    }
}

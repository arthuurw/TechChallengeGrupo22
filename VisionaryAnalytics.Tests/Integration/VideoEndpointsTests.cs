using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Messaging;
using VisionaryAnalytics.Infrastructure.Models;

namespace VisionaryAnalytics.Tests.Integration;

public class TestesEndpointsVideo : IClassFixture<FabricaApiVideo>
{
    private readonly FabricaApiVideo _fabrica;
    private readonly HttpClient _cliente;

    public TestesEndpointsVideo(FabricaApiVideo fabrica)
    {
        _fabrica = fabrica;
        _cliente = fabrica.CreateClient();
    }

    [Fact]
    public async Task PostVideos_DeveRetornarBadRequestQuandoCorpoNaoEhMultipart()
    {
        var resposta = await _cliente.PostAsync("/videos", new StringContent("{}", Encoding.UTF8, "application/json"));

        resposta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var conteudoResposta = await resposta.Content.ReadAsStringAsync();
        conteudoResposta.Should().Contain("form-data");
    }

    [Fact]
    public async Task PostVideos_DeveArmazenarTrabalhoEPublicarMensagem()
    {
        using var conteudo = new MultipartFormDataContent();
        var arquivo = new ByteArrayContent(Encoding.UTF8.GetBytes("conteudo de video falso"));
        arquivo.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        conteudo.Add(arquivo, "file", "exemplo.mp4");
        conteudo.Add(new StringContent("15"), "fps");

        var resposta = await _cliente.PostAsync("/videos", conteudo);
        resposta.StatusCode.Should().Be(HttpStatusCode.OK);

        using var fluxoResposta = await resposta.Content.ReadAsStreamAsync();
        using var documento = await JsonDocument.ParseAsync(fluxoResposta);
        var jobId = documento.RootElement.GetProperty("jobId").GetGuid();

        documento.RootElement.GetProperty("status").GetString().Should().Be(VideoJobStatuses.Queued);
        _fabrica.Armazenamento.Trabalhos.Should().ContainKey(jobId);
        _fabrica.Publicador.Mensagens.Should().ContainSingle(mensagem => mensagem.JobId == jobId && mensagem.Fps == 15);

        var arquivoSalvo = Path.Combine(_fabrica.CaminhoUpload, $"{jobId}.mp4");
        File.Exists(arquivoSalvo).Should().BeTrue();
        File.Delete(arquivoSalvo);
    }

    [Fact]
    public async Task GetStatus_DeveRetornarNotFoundParaTrabalhoDesconhecido()
    {
        var resposta = await _cliente.GetAsync($"/videos/{Guid.NewGuid()}/status");

        resposta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetStatus_DeveRetornarInformacoesArmazenadas()
    {
        var jobId = Guid.NewGuid();
        await _fabrica.Armazenamento.InitAsync(jobId, "filme.mp4", 12);
        await _fabrica.Armazenamento.SetStatusAsync(jobId, VideoJobStatuses.Processing, "convertendo");

        var resposta = await _cliente.GetAsync($"/videos/{jobId}/status");
        resposta.StatusCode.Should().Be(HttpStatusCode.OK);

        using var documento = await JsonDocument.ParseAsync(await resposta.Content.ReadAsStreamAsync());
        documento.RootElement.GetProperty("jobId").GetGuid().Should().Be(jobId);
        documento.RootElement.GetProperty("status").GetString().Should().Be(VideoJobStatuses.Processing);
        documento.RootElement.GetProperty("errorMessage").GetString().Should().Be("convertendo");
        var metadados = documento.RootElement.GetProperty("metadata");
        metadados.GetProperty("nomeArquivo").GetString().Should().Be("filme.mp4");
        metadados.GetProperty("quadrosPorSegundo").GetDouble().Should().Be(12);
    }

    [Fact]
    public async Task GetResults_DeveRetornarResultadosArmazenados()
    {
        var jobId = Guid.NewGuid();
        await _fabrica.Armazenamento.InitAsync(jobId, "filme.mp4", 10);
        await _fabrica.Armazenamento.SetStatusAsync(jobId, VideoJobStatuses.Completed);
        await _fabrica.Armazenamento.AddResultAsync(jobId, new VideoJobResult("quadro1", 1.5));
        await _fabrica.Armazenamento.AddResultAsync(jobId, new VideoJobResult("quadro2", 3.2));

        var resposta = await _cliente.GetAsync($"/videos/{jobId}/results");
        resposta.StatusCode.Should().Be(HttpStatusCode.OK);

        using var documento = await JsonDocument.ParseAsync(await resposta.Content.ReadAsStreamAsync());
        documento.RootElement.GetProperty("jobId").GetGuid().Should().Be(jobId);
        documento.RootElement.GetProperty("status").GetString().Should().Be(VideoJobStatuses.Completed);
        var resultados = documento.RootElement.GetProperty("results");
        resultados.GetArrayLength().Should().Be(2);
        resultados[0].GetProperty("conteudo").GetString().Should().Be("quadro1");
        resultados[1].GetProperty("conteudo").GetString().Should().Be("quadro2");
    }
}

public class FabricaApiVideo : WebApplicationFactory<Program>
{
    private readonly string _caminhoUploadInterno = Path.Combine(Path.GetTempPath(), "VisionaryAnalyticsTests", Guid.NewGuid().ToString("N"));

    public ArmazenamentoVideoFalso Armazenamento { get; } = new();
    public PublicadorRabbitMqFalso Publicador { get; } = new();
    public string CaminhoUpload => _caminhoUploadInterno;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UPLOAD_PATH"] = _caminhoUploadInterno
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll(typeof(IRabbitMqPublisher));
            services.RemoveAll(typeof(IVideoJobStore));
            services.RemoveAll(typeof(IConnectionMultiplexer));

            services.AddSingleton<IVideoJobStore>(_ => Armazenamento);
            services.AddSingleton<IRabbitMqPublisher>(_ => Publicador);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_caminhoUploadInterno))
        {
            Directory.Delete(_caminhoUploadInterno, true);
        }
    }
}

public sealed class ArmazenamentoVideoFalso : IVideoJobStore
{
    private readonly ConcurrentDictionary<Guid, DadosTrabalhoFalso> _trabalhos = new();

    public IReadOnlyDictionary<Guid, DadosTrabalhoFalso> Trabalhos => _trabalhos;

    public Task InitAsync(Guid jobId, string nomeArquivo, double fps, CancellationToken cancellationToken = default)
    {
        var trabalho = _trabalhos.GetOrAdd(jobId, _ => new DadosTrabalhoFalso());
        trabalho.Metadata = new VideoJobMetadata(nomeArquivo, fps, DateTimeOffset.UtcNow);
        trabalho.Status = new VideoJobState(VideoJobStatuses.Queued, null);
        trabalho.Resultados = new List<VideoJobResult>();
        return Task.CompletedTask;
    }

    public Task SetStatusAsync(Guid jobId, string status, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        var trabalho = _trabalhos.GetOrAdd(jobId, _ => new DadosTrabalhoFalso());
        trabalho.Status = new VideoJobState(status, errorMessage);
        return Task.CompletedTask;
    }

    public Task<VideoJobState?> GetStatusAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_trabalhos.TryGetValue(jobId, out var trabalho))
        {
            return Task.FromResult<VideoJobState?>(trabalho.Status);
        }

        return Task.FromResult<VideoJobState?>(null);
    }

    public Task AddResultAsync(Guid jobId, VideoJobResult resultado, CancellationToken cancellationToken = default)
    {
        var trabalho = _trabalhos.GetOrAdd(jobId, _ => new DadosTrabalhoFalso());
        trabalho.Resultados ??= new List<VideoJobResult>();
        trabalho.Resultados.Add(resultado);
        trabalho.Resultados.Sort((left, right) => left.InstanteSegundos.CompareTo(right.InstanteSegundos));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VideoJobResult>> GetResultsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_trabalhos.TryGetValue(jobId, out var trabalho) && trabalho.Resultados is { } resultados)
        {
            return Task.FromResult<IReadOnlyList<VideoJobResult>>(resultados.ToList());
        }

        return Task.FromResult<IReadOnlyList<VideoJobResult>>(Array.Empty<VideoJobResult>());
    }

    public Task<VideoJobMetadata?> GetMetadataAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_trabalhos.TryGetValue(jobId, out var trabalho))
        {
            return Task.FromResult(trabalho.Metadata);
        }

        return Task.FromResult<VideoJobMetadata?>(null);
    }

    public sealed class DadosTrabalhoFalso
    {
        public VideoJobState? Status { get; set; }
        public VideoJobMetadata? Metadata { get; set; }
        public List<VideoJobResult>? Resultados { get; set; }
    }
}

public sealed class PublicadorRabbitMqFalso : IRabbitMqPublisher
{
    private readonly List<VideoJobMessage> _mensagens = new();

    public IReadOnlyList<VideoJobMessage> Mensagens => _mensagens;

    public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        if (message is VideoJobMessage typed)
        {
            _mensagens.Add(typed);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

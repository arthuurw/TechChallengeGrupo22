using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using VisionaryAnalytics.Infrastructure.Interface;
using VisionaryAnalytics.Infrastructure.Messaging;
using VisionaryAnalytics.Infrastructure.Models;
using VisionaryAnalytics.Worker.Notifications;
using VisionaryAnalytics.Worker.Options;
using VisionaryAnalytics.Worker.Processing;

namespace VisionaryAnalytics.Tests.Unit;

public class TestesVideoJobProcessor
{
    private static (VideoJobProcessor processor, Mock<IVideoJobStore> storeMock, Mock<IProcessingNotifier> notifierMock, string caminhoVideo, List<string> diretoriosLimpos) CriarSut(
        Func<VideoJobMessage, string, CancellationToken, Task>? extrator = null,
        Func<string, int, double, CancellationToken, ValueTask<VideoJobResult?>>? decodificador = null)
    {
        var logger = Mock.Of<ILogger<VideoJobProcessor>>();
        var storeMock = new Mock<IVideoJobStore>();
        var notifierMock = new Mock<IProcessingNotifier>();
        var opcoes = Options.Create(new VideoProcessingOptions { MaxDegreeOfParallelism = 1 });
        var diretoriosLimpos = new List<string>();

        extrator ??= async (mensagem, diretorio, _) =>
        {
            Directory.CreateDirectory(diretorio);
            var caminho1 = Path.Combine(diretorio, "frame_00001.png");
            var caminho2 = Path.Combine(diretorio, "frame_00002.png");
            await File.WriteAllTextAsync(caminho1, "frame1");
            await File.WriteAllTextAsync(caminho2, "frame2");
        };

        decodificador ??= (caminho, indice, fps, _) =>
        {
            if (caminho.EndsWith("00001.png"))
            {
                return ValueTask.FromResult<VideoJobResult?>(new VideoJobResult("conteudo", Math.Round(indice / Math.Max(fps, 1d), 3)));
            }

            return ValueTask.FromResult<VideoJobResult?>(null);
        };

        var processor = new VideoJobProcessor(
            logger,
            storeMock.Object,
            notifierMock.Object,
            opcoes,
            extrator,
            decodificador,
            diretorio =>
            {
                diretoriosLimpos.Add(diretorio);
                if (Directory.Exists(diretorio))
                {
                    Directory.Delete(diretorio, true);
                }
            });

        var caminhoVideo = Path.Combine(Path.GetTempPath(), $"video_{Guid.NewGuid():N}.mp4");
        File.WriteAllText(caminhoVideo, "arquivo de teste");

        return (processor, storeMock, notifierMock, caminhoVideo, diretoriosLimpos);
    }

    [Fact]
    public async Task ProcessAsync_DeveProcessarComSucesso()
    {
        var (processor, storeMock, notifierMock, caminhoVideo, diretoriosLimpos) = CriarSut();
        var mensagem = new VideoJobMessage(Guid.NewGuid(), caminhoVideo, 10);

        try
        {
            var resultados = await processor.ProcessAsync(mensagem, CancellationToken.None);

            resultados.Should().Be(1);
            storeMock.Verify(s => s.SetStatusAsync(mensagem.JobId, VideoJobStatuses.Processing, null, It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.AddResultAsync(mensagem.JobId, It.Is<VideoJobResult>(r => r.Conteudo == "conteudo" && r.InstanteSegundos == 0), It.IsAny<CancellationToken>()), Times.Once);
            storeMock.Verify(s => s.SetStatusAsync(mensagem.JobId, VideoJobStatuses.Completed, null, It.IsAny<CancellationToken>()), Times.Once);
            notifierMock.Verify(n => n.NotifyCompletedAsync(mensagem.JobId, 1, It.IsAny<CancellationToken>()), Times.Once);
            diretoriosLimpos.Should().NotBeEmpty();
        }
        finally
        {
            if (File.Exists(caminhoVideo))
            {
                File.Delete(caminhoVideo);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_DeveRetornarZeroQuandoArquivoInexistente()
    {
        var (processor, storeMock, notifierMock, _, _) = CriarSut();
        var mensagem = new VideoJobMessage(Guid.NewGuid(), Path.Combine(Path.GetTempPath(), "inexistente.mp4"), 5);

        var resultados = await processor.ProcessAsync(mensagem, CancellationToken.None);

        resultados.Should().Be(0);
        storeMock.Verify(s => s.SetStatusAsync(mensagem.JobId, VideoJobStatuses.Failed, It.Is<string>(erro => !string.IsNullOrWhiteSpace(erro)), It.IsAny<CancellationToken>()), Times.Once);
        notifierMock.Verify(n => n.NotifyFailedAsync(mensagem.JobId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_DeveLancarQuandoCancelado()
    {
        var (processor, storeMock, notifierMock, caminhoVideo, diretoriosLimpos) = CriarSut(
            extrator: (_, _, _) => throw new OperationCanceledException());
        var mensagem = new VideoJobMessage(Guid.NewGuid(), caminhoVideo, 8);

        try
        {
            await processor.Invoking(p => p.ProcessAsync(mensagem, new CancellationToken(true)))
                .Should().ThrowAsync<OperationCanceledException>();

            storeMock.Verify(s => s.SetStatusAsync(mensagem.JobId, VideoJobStatuses.Processing, null, It.IsAny<CancellationToken>()), Times.Once);
            notifierMock.Verify(n => n.NotifyFailedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            diretoriosLimpos.Should().NotBeEmpty();
        }
        finally
        {
            if (File.Exists(caminhoVideo))
            {
                File.Delete(caminhoVideo);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_DeveRelancarExcecaoGenerica()
    {
        var (processor, storeMock, notifierMock, caminhoVideo, diretoriosLimpos) = CriarSut(
            extrator: (_, _, _) => throw new InvalidOperationException("falha geral"));
        var mensagem = new VideoJobMessage(Guid.NewGuid(), caminhoVideo, 12);

        try
        {
            await processor.Invoking(p => p.ProcessAsync(mensagem, CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>();

            storeMock.Verify(s => s.SetStatusAsync(mensagem.JobId, VideoJobStatuses.Failed, "falha geral", It.IsAny<CancellationToken>()), Times.Once);
            notifierMock.Verify(n => n.NotifyFailedAsync(mensagem.JobId, "falha geral", It.IsAny<CancellationToken>()), Times.Once);
            diretoriosLimpos.Should().NotBeEmpty();
        }
        finally
        {
            if (File.Exists(caminhoVideo))
            {
                File.Delete(caminhoVideo);
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_DeveTratarAusenciaDeQuadros()
    {
        var (processor, storeMock, notifierMock, caminhoVideo, _) = CriarSut(
            extrator: (_, diretorio, _) =>
            {
                Directory.CreateDirectory(diretorio);
                return Task.CompletedTask;
            });
        var mensagem = new VideoJobMessage(Guid.NewGuid(), caminhoVideo, 10);

        try
        {
            var resultados = await processor.ProcessAsync(mensagem, CancellationToken.None);

            resultados.Should().Be(0);
            storeMock.Verify(s => s.SetStatusAsync(mensagem.JobId, VideoJobStatuses.Completed, null, It.IsAny<CancellationToken>()), Times.Once);
            notifierMock.Verify(n => n.NotifyCompletedAsync(mensagem.JobId, 0, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(caminhoVideo))
            {
                File.Delete(caminhoVideo);
            }
        }
    }
}

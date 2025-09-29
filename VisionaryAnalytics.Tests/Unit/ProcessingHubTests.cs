using Microsoft.AspNetCore.SignalR;
using Moq;
using VisionaryAnalytics.Api;

namespace VisionaryAnalytics.Tests.Unit;

public class TestesProcessingHub
{
    [Fact]
    public async Task SubscribeToJob_DeveAdicionarConexaoAoGrupo()
    {
        var hub = new ProcessingHub();
        var connectionId = "conexao-teste";
        var groupsMock = new Mock<IGroupManager>();
        var contextMock = new Mock<HubCallerContext>();
        contextMock.SetupGet(c => c.ConnectionId).Returns(connectionId);

        hub.Context = contextMock.Object;
        hub.Groups = groupsMock.Object;

        var jobId = Guid.NewGuid();
        await hub.SubscribeToJob(jobId);

        groupsMock.Verify(g => g.AddToGroupAsync(connectionId, jobId.ToString(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyCompleted_DeveEnviarMensagemParaGrupo()
    {
        var hub = new ProcessingHub();
        var groupProxy = new Mock<IClientProxy>();
        var clientsMock = new Mock<IHubCallerClients>();
        var connectionId = "conexao";
        var contextMock = new Mock<HubCallerContext>();
        contextMock.SetupGet(c => c.ConnectionId).Returns(connectionId);

        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);

        hub.Context = contextMock.Object;
        hub.Clients = clientsMock.Object;
        hub.Groups = Mock.Of<IGroupManager>();

        var jobId = Guid.NewGuid();
        await hub.NotifyCompleted(jobId, 3);

        clientsMock.Verify(c => c.Group(jobId.ToString()), Times.Once);
        groupProxy.Verify(proxy => proxy.SendCoreAsync(
            "processingCompleted",
            It.Is<object[]>(payload =>
                payload.Length == 1 &&
                payload[0] is { } obj &&
                obj.GetType().GetProperty("jobId")?.GetValue(obj).Equals(jobId) == true &&
                obj.GetType().GetProperty("resultsCount")?.GetValue(obj).Equals(3) == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyFailed_DeveEnviarMensagemDeFalha()
    {
        var hub = new ProcessingHub();
        var groupProxy = new Mock<IClientProxy>();
        var clientsMock = new Mock<IHubCallerClients>();
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(groupProxy.Object);

        hub.Clients = clientsMock.Object;
        hub.Groups = Mock.Of<IGroupManager>();
        hub.Context = Mock.Of<HubCallerContext>(c => c.ConnectionId == "abc");

        var jobId = Guid.NewGuid();
        await hub.NotifyFailed(jobId, "erro grave");

        groupProxy.Verify(proxy => proxy.SendCoreAsync(
            "processingFailed",
            It.Is<object[]>(payload =>
                payload.Length == 1 &&
                payload[0] is { } obj &&
                obj.GetType().GetProperty("jobId")?.GetValue(obj).Equals(jobId) == true &&
                obj.GetType().GetProperty("errorMessage")?.GetValue(obj).Equals("erro grave") == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

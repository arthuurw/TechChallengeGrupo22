using System;
using System.Threading;
using System.Threading.Tasks;

namespace VisionaryAnalytics.Worker.Notifications;

public interface IProcessingNotifier
{
    Task NotificarConclusaoAsync(Guid jobId, int resultsCount, CancellationToken cancellationToken = default);
}

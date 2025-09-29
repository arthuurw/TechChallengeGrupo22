namespace VisionaryAnalytics.Worker.Notifications;

public interface IHubConnectionFactory
{
    IHubConnectionContext Create(string hubUrl);
}

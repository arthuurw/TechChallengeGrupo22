namespace VisionaryAnalytics.Worker.Options;

public sealed class SignalROptions
{
    public bool EnableNotifications { get; set; } = true;
    public string HubUrl { get; set; } = "http://api:8080/hubs/processing";
}

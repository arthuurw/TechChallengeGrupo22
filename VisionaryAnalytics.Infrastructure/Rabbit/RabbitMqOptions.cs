namespace VisionaryAnalytics.Infrastructure.Rabbit;

public sealed class RabbitMqOptions
{
    public string HostName { get; set; } = "rabbitmq";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string QueueName { get; set; } = "video-jobs";
    public ushort PrefetchCount { get; set; } = 1;
}

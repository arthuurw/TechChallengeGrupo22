namespace VisionaryAnalytics.Infrastructure.Rabbit;

public record RabbitMqOptions
{
    public string HostName { get; init; } = "rabbitmq";
    public string UserName { get; init; } = "guest";
    public string Password { get; init; } = "guest";
    public string QueueName { get; init; } = "video-jobs";
}
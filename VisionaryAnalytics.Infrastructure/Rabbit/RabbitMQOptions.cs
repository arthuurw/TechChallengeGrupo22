using System.ComponentModel.DataAnnotations;

namespace VisionaryAnalytics.Infrastructure.Rabbit;

public record RabbitMqOptions
{
    [Required]
    [MinLength(1)]
    public string HostName { get; set; } = "rabbitmq";

    [Required]
    [MinLength(1)]
    public string UserName { get; set; } = "guest";

    [Required]
    [MinLength(1)]
    public string Password { get; set; } = "guest";

    [Required]
    [MinLength(1)]
    public string QueueName { get; set; } = "video-jobs";
}

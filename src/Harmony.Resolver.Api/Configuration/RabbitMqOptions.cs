namespace Harmony.Resolver.Api.Configuration;

/// <summary>
/// RabbitMQ connection for publishing job "doorbell" notifications to the downloader fleet. Bound from
/// the <c>RabbitMq</c> configuration section. When <see cref="Uri"/> is empty the resolver uses a no-op
/// notifier (dev/Inline and tests run without a broker).
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>AMQP connection string, e.g. <c>amqp://user:pass@rabbitmq:5672</c> (internal, plaintext).</summary>
    public string Uri { get; init; } = string.Empty;

    /// <summary>Durable work queue the downloaders consume from.</summary>
    public string QueueName { get; init; } = "harmony.ingest.jobs";

    public bool Enabled => !string.IsNullOrWhiteSpace(Uri);
}

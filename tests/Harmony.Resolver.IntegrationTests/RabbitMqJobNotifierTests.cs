using System.Text.Json;
using Harmony.Resolver.Api.Configuration;
using Harmony.Resolver.Api.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Testcontainers.RabbitMq;
using Xunit;

namespace Harmony.Resolver.IntegrationTests;

/// <summary>
/// Round-trips a doorbell through a real RabbitMQ broker: <see cref="RabbitMqJobNotifier"/> publishes to
/// the durable queue and a plain consumer reads the video id back out.
/// </summary>
public sealed class RabbitMqJobNotifierTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    public async Task InitializeAsync() => await _rabbit.StartAsync();

    public async Task DisposeAsync() => await _rabbit.DisposeAsync();

    [Fact]
    public async Task Publishes_the_video_id_to_the_durable_queue()
    {
        var options = new RabbitMqOptions { Uri = _rabbit.GetConnectionString(), QueueName = "harmony.ingest.jobs.test" };
        await using var notifier = new RabbitMqJobNotifier(options, NullLogger<RabbitMqJobNotifier>.Instance);

        await notifier.NotifyAsync("dQw4w9WgXcQ", CancellationToken.None);

        var factory = new ConnectionFactory { Uri = new Uri(options.Uri) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(options.QueueName, durable: true, exclusive: false, autoDelete: false);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            using var json = JsonDocument.Parse(delivery.Body.ToArray());
            received.TrySetResult(json.RootElement.GetProperty("videoId").GetString()!);
            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(options.QueueName, autoAck: true, consumer);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(received.Task, completed);
        Assert.Equal("dQw4w9WgXcQ", await received.Task);
    }
}

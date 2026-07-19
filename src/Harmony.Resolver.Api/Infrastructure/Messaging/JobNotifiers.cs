using System.Text.Json;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using RabbitMQ.Client;

namespace Harmony.Resolver.Api.Infrastructure.Messaging;

/// <summary>Used when no broker is configured (Inline/dev and tests): notifications are silently dropped.</summary>
public sealed class NoopJobNotifier : IJobNotifier
{
    public Task NotifyAsync(string videoId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Publishes a persistent doorbell message to a durable RabbitMQ work queue. Holds one lazily-opened,
/// self-recovering connection/channel; a publish failure is logged (never thrown) and invalidates the
/// channel so the next call reconnects. The durable Postgres job + the republisher make lost messages
/// harmless, so best-effort delivery is sufficient.
/// </summary>
public sealed class RabbitMqJobNotifier(RabbitMqOptions options, ILogger<RabbitMqJobNotifier> logger)
    : IJobNotifier, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task NotifyAsync(string videoId, CancellationToken cancellationToken)
    {
        try
        {
            var channel = await EnsureChannelAsync(cancellationToken);
            var body = JsonSerializer.SerializeToUtf8Bytes(new JobMessage(videoId), JsonSerializerOptions.Web);
            var properties = new BasicProperties { Persistent = true, ContentType = "application/json" };
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: options.QueueName,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to publish job notification for {VideoId}; the republisher will re-ring.", videoId);
            await InvalidateAsync();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true }) return _channel;
            await DisposeConnectionAsync();
            var factory = new ConnectionFactory { Uri = new Uri(options.Uri), AutomaticRecoveryEnabled = true };
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
            await _channel.QueueDeclareAsync(options.QueueName, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: cancellationToken);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InvalidateAsync()
    {
        await _gate.WaitAsync();
        try { await DisposeConnectionAsync(); }
        finally { _gate.Release(); }
    }

    private async Task DisposeConnectionAsync()
    {
        try { if (_channel is not null) await _channel.DisposeAsync(); } catch { /* best effort */ }
        try { if (_connection is not null) await _connection.DisposeAsync(); } catch { /* best effort */ }
        _channel = null;
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync();
        _gate.Dispose();
    }

    private sealed record JobMessage(string VideoId);
}

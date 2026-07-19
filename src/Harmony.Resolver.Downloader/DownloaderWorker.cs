using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Harmony.Resolver.Downloader;

/// <summary>
/// The agent loop: claim a pending ingestion job from the resolver, fetch its audio with yt-dlp on this
/// residential IP, and upload the raw file for the resolver to normalize and cache. Each job keeps its
/// lease alive with heartbeats while downloading.
///
/// When <see cref="DownloaderOptions.RabbitMqEnabled"/>, the agent subscribes to the resolver's durable
/// job queue and reacts to each "doorbell" by claiming a job — the message is only a wake-up, the DB
/// lease still arbitrates ownership. Without a broker it falls back to a fixed-interval claim poll.
/// </summary>
public sealed class DownloaderWorker(
    ResolverWorkerClient client,
    YtDlpDownloader downloader,
    DownloaderOptions options,
    ILogger<DownloaderWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _drainGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Downloader agent starting: resolver={Resolver}, parallel={Parallel}, auth={Auth}, transport={Transport}.",
            options.ResolverBaseUrl, options.MaxParallel, options.Auth0Enabled ? "auth0-m2m" : "anonymous",
            options.RabbitMqEnabled ? "rabbitmq" : "poll");

        if (options.RabbitMqEnabled)
            await RunSubscriberAsync(stoppingToken);
        else
            await Task.WhenAll(Enumerable.Range(0, options.MaxParallel).Select(index => RunPollLoopAsync(index, stoppingToken)));
    }

    private async Task RunSubscriberAsync(CancellationToken stoppingToken)
    {
        var prefetch = (ushort)Math.Max(1, options.MaxParallel);
        var factory = CreateConnectionFactory(options, prefetch);
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(options.RabbitMqQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, prefetch, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                var job = await client.ClaimAsync(stoppingToken);
                if (job is not null) await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Error handling a job notification.");
            }
            finally
            {
                try { await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken); }
                catch (Exception ackException) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ackException, "Failed to ack delivery {Tag}.", delivery.DeliveryTag);
                }
            }
        };
        connection.RecoverySucceededAsync += async (_, _) =>
        {
            logger.LogInformation("Reconnected to the broker; draining any backlog.");
            await DrainAsync(stoppingToken);
        };

        await channel.BasicConsumeAsync(options.RabbitMqQueue, autoAck: false, consumer, cancellationToken: stoppingToken);
        logger.LogInformation("Subscribed to {Queue}; draining startup backlog.", options.RabbitMqQueue);
        await DrainAsync(stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    internal static ConnectionFactory CreateConnectionFactory(DownloaderOptions options, ushort concurrency)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.RabbitMqUri!),
            AutomaticRecoveryEnabled = true,
            ConsumerDispatchConcurrency = concurrency
        };
        if (!string.IsNullOrWhiteSpace(options.RabbitMqCertificateSha256))
        {
            if (!factory.Ssl.Enabled)
                throw new InvalidOperationException("RABBITMQ_CERT_SHA256 requires an amqps:// RABBITMQ_URI.");
            var expectedFingerprint = ParseCertificateFingerprint(options.RabbitMqCertificateSha256);
            factory.Ssl.CertificateValidationCallback = (_, certificate, _, _) =>
                CertificateMatches(certificate, expectedFingerprint);
        }
        return factory;
    }

    private static byte[] ParseCertificateFingerprint(string value)
    {
        var normalized = value.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        try
        {
            var fingerprint = Convert.FromHexString(normalized);
            if (fingerprint.Length == SHA256.HashSizeInBytes) return fingerprint;
        }
        catch (FormatException)
        {
            // Report one stable configuration error below.
        }
        throw new InvalidOperationException(
            "RABBITMQ_CERT_SHA256 must be a 64-character SHA-256 certificate fingerprint.");
    }

    private static bool CertificateMatches(X509Certificate? certificate, byte[] expectedFingerprint)
    {
        if (certificate is null) return false;
        var actualFingerprint = SHA256.HashData(certificate.GetRawCertData());
        return CryptographicOperations.FixedTimeEquals(actualFingerprint, expectedFingerprint);
    }

    // Claims and processes every currently-pending job until the queue is empty. Runs once on startup and
    // on each reconnect so a downloader that was offline catches a backlog without waiting for re-rings.
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        if (!await _drainGate.WaitAsync(0, cancellationToken)) return; // a drain is already in progress
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var job = await client.ClaimAsync(cancellationToken);
                if (job is null) break;
                await ProcessAsync(job, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Backlog drain failed.");
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private async Task RunPollLoopAsync(int index, CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(options.PollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await client.ClaimAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(idleDelay, stoppingToken);
                    continue;
                }
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Runner {Index} error; backing off.", index);
                try { await Task.Delay(idleDelay, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ProcessAsync(WorkerJob job, CancellationToken stoppingToken)
    {
        logger.LogInformation("Claimed {VideoId}.", job.VideoId);
        var workingDirectory = Directory.CreateTempSubdirectory("harmony-downloader-").FullName;
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = HeartbeatLoopAsync(job, jobCts);
        try
        {
            string filePath;
            try
            {
                filePath = await downloader.DownloadAsync(job.VideoId, workingDirectory, jobCts.Token);
            }
            catch (DownloadException exception)
            {
                logger.LogWarning("Download failed for {VideoId}: {Code} ({Detail}).", job.VideoId, exception.Code, exception.Detail);
                if (!jobCts.IsCancellationRequested)
                    await client.FailAsync(job.VideoId, job.LeaseToken, exception.Code, stoppingToken);
                return;
            }

            await client.UploadAsync(job.VideoId, job.LeaseToken, filePath, stoppingToken);
            logger.LogInformation("Uploaded {VideoId}.", job.VideoId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Processing {VideoId} failed.", job.VideoId);
            if (!jobCts.IsCancellationRequested)
                try { await client.FailAsync(job.VideoId, job.LeaseToken, "worker_error", CancellationToken.None); }
                catch (Exception failException) { logger.LogWarning(failException, "Could not report failure for {VideoId}.", job.VideoId); }
        }
        finally
        {
            await jobCts.CancelAsync();
            try { await heartbeat; } catch { /* already logged */ }
            TryDelete(workingDirectory);
        }
    }

    private async Task HeartbeatLoopAsync(WorkerJob job, CancellationTokenSource jobCts)
    {
        var token = jobCts.Token;
        // Renew at roughly half the lease window so a slow download never lets the lease lapse.
        var interval = TimeSpan.FromSeconds(Math.Clamp((job.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds / 2, 15, 120));
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token);
                if (!await client.HeartbeatAsync(job.VideoId, job.LeaseToken, token))
                {
                    logger.LogWarning("Lost lease on {VideoId}; abandoning the job.", job.VideoId);
                    await jobCts.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished (or shutdown) and cancelled the heartbeat; expected.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Heartbeat error for {VideoId}.", job.VideoId);
        }
    }

    private void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not clean up {Directory}.", directory); }
    }
}

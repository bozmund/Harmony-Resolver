namespace Harmony.Resolver.Api.Abstractions;

/// <summary>
/// Publishes a lightweight "a job is pending" notification so idle downloaders react immediately instead
/// of polling. The message is a doorbell — the durable Postgres job row and its lease remain the source
/// of truth, so a dropped or duplicated notification is harmless (the fleet claims via the DB, and the
/// republisher re-rings). Implementations must never throw into the caller's request path.
/// </summary>
public interface IJobNotifier
{
    Task NotifyAsync(string videoId, CancellationToken cancellationToken);
}

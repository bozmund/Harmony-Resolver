using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Harmony.Resolver.Downloader;

public sealed record WorkerJob(
    string VideoId,
    Guid LeaseToken,
    DateTimeOffset ExpiresAt,
    string Kind = "download");

/// <summary>
/// HTTP client for the resolver's <c>/v1/worker/*</c> endpoints. Every request carries the Auth0 M2M
/// bearer token (when configured) and, for job-scoped calls, the lease token in <c>X-Lease-Token</c>.
/// </summary>
public sealed class ResolverWorkerClient(HttpClient http, Auth0TokenProvider tokens)
{
    private const string LeaseHeader = "X-Lease-Token";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WorkerJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/worker/jobs/claim");
        await AuthorizeAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WorkerJob>(Json, cancellationToken);
    }

    /// <summary>Renews the lease. Returns false when the lease was lost (HTTP 409) so the caller stops working the job.</summary>
    public async Task<bool> HeartbeatAsync(string videoId, Guid leaseToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/worker/jobs/{videoId}/heartbeat");
        request.Headers.TryAddWithoutValidation(LeaseHeader, leaseToken.ToString());
        await AuthorizeAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task UploadAsync(string videoId, Guid leaseToken, string filePath, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(filePath);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"v1/worker/tracks/{videoId}/audio") { Content = content };
        request.Headers.TryAddWithoutValidation(LeaseHeader, leaseToken.ToString());
        await AuthorizeAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task FailAsync(string videoId, Guid leaseToken, string code, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/worker/tracks/{videoId}/fail")
        {
            Content = JsonContent.Create(new { code }, options: Json)
        };
        request.Headers.TryAddWithoutValidation(LeaseHeader, leaseToken.ToString());
        await AuthorizeAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        // Best-effort: a lost lease (409) just means someone else owns the job now.
        if (response.StatusCode != HttpStatusCode.Conflict) response.EnsureSuccessStatusCode();
    }

    public async Task VerifyBackupAsync(
        string videoId,
        Guid leaseToken,
        SourceFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"v1/worker/tracks/{videoId}/verify-backup")
        {
            Content = JsonContent.Create(new
            {
                fingerprint.DurationSeconds,
                fingerprint.FingerprintA,
                fingerprint.FingerprintB
            }, options: Json)
        };
        request.Headers.TryAddWithoutValidation(LeaseHeader, leaseToken.ToString());
        await AuthorizeAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (await tokens.GetAsync(cancellationToken) is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

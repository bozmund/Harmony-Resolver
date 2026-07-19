using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Harmony.Resolver.Downloader;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class RabbitMqCertificatePinningTests
{
    [Fact]
    public void AcceptsOnlyThePinnedCertificate()
    {
        using var expected = CreateCertificate("expected");
        using var other = CreateCertificate("other");
        var fingerprint = Convert.ToHexString(SHA256.HashData(expected.RawData));
        var options = new DownloaderOptions
        {
            ResolverBaseUrl = "https://resolver.example",
            RabbitMqUri = "amqps://downloader:password@broker.example:5671/",
            RabbitMqCertificateSha256 = fingerprint
        };

        var factory = DownloaderWorker.CreateConnectionFactory(options, 1);
        var callback = Assert.IsType<RemoteCertificateValidationCallback>(
            factory.Ssl.CertificateValidationCallback);

        Assert.True(callback(new object(), expected, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(callback(new object(), other, null, SslPolicyErrors.None));
    }

    [Fact]
    public void RejectsPinningOnPlaintextConnection()
    {
        var options = new DownloaderOptions
        {
            ResolverBaseUrl = "https://resolver.example",
            RabbitMqUri = "amqp://downloader:password@broker.example:5672/",
            RabbitMqCertificateSha256 = new string('0', 64)
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => DownloaderWorker.CreateConnectionFactory(options, 1));

        Assert.Contains("amqps://", exception.Message);
    }

    private static X509Certificate2 CreateCertificate(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
    }
}

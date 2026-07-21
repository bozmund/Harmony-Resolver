using Harmony.Resolver.Downloader;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class DownloaderDurationLimitTests
{
    [Fact]
    public void DefaultsToNineMinutesAndPassesLimitToYtDlp()
    {
        var options = new DownloaderOptions { ResolverBaseUrl = "http://localhost" };

        var arguments = YtDlpDownloader.BuildArguments(
            "jNQXAC9IVRw", "source.%(ext)s", options.MaxDurationMinutes);

        Assert.Equal(9, options.MaxDurationMinutes);
        var filterIndex = arguments.IndexOf("--match-filter");
        Assert.True(filterIndex >= 0);
        Assert.Equal("duration <= 540", arguments[filterIndex + 1]);
    }

    [Fact]
    public void FingerprintFailureIsSpecificAndRedactsSignedStreamUrls()
    {
        const string detail = "Error opening input https://media.example/audio?expire=1&signature=secret: Server returned 403 Forbidden";

        var exception = new DownloadException(
            DownloadFailure.ProcessFailureCode("source_fingerprint_a", "ffmpeg", detail),
            detail, stage: "source_fingerprint_a", tool: "ffmpeg", exitCode: 1);

        Assert.Equal("source_fingerprint_ffmpeg_access_denied", exception.Code);
        Assert.Equal("source_fingerprint_a", exception.Stage);
        Assert.Equal("ffmpeg", exception.Tool);
        Assert.Equal(1, exception.ExitCode);
        Assert.DoesNotContain("signature=secret", exception.Detail);
        Assert.Contains("[redacted-url]", exception.Detail);
    }
}

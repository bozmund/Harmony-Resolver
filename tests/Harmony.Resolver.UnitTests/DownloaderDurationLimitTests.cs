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
    public void RequestsMetadataAndFilePathAsSeparatePrintLines()
    {
        var arguments = YtDlpDownloader.BuildArguments("jNQXAC9IVRw", "source.%(ext)s", 9);

        Assert.Contains(YtDlpMetadataLine.PrintTemplate, arguments);
        Assert.Contains("after_move:filepath", arguments);
        // Two --print flags, each immediately followed by its template.
        Assert.Equal(2, arguments.Count(x => x == "--print"));
    }

    [Fact]
    public void MetadataBackfillNeverSelectsOrWritesAudio()
    {
        var arguments = YtDlpDownloader.BuildMetadataArguments("jNQXAC9IVRw");

        Assert.Contains("--skip-download", arguments);
        Assert.Contains(YtDlpMetadataLine.PrintTemplate, arguments);
        Assert.DoesNotContain("-f", arguments);
        Assert.DoesNotContain("-o", arguments);
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

    [Theory]
    [InlineData("ERROR: [youtube] abcdefghijk: Video unavailable.")]
    [InlineData("error: VIDEO UNAVAILABLE. This video is not available.")]
    public void MetadataFailureMarksExplicitUnavailableVideosForLongRetry(string stderr)
    {
        Assert.Equal("metadata_yt_dlp_unavailable", DownloadFailure.MetadataFailureCode(stderr));
    }

    [Fact]
    public void MetadataFailureKeepsOtherYtDlpFailuresGeneric()
    {
        Assert.Equal("metadata_yt_dlp_failed",
            DownloadFailure.MetadataFailureCode("ERROR: Sign in to confirm you're not a bot"));
    }
}

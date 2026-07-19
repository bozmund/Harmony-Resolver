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
}

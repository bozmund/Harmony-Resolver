using Xunit;
using ApiMetadataLine = Harmony.Resolver.Api.Infrastructure.Extraction.YtDlpMetadataLine;
using DownloaderMetadataLine = Harmony.Resolver.Downloader.YtDlpMetadataLine;

namespace Harmony.Resolver.UnitTests;

public sealed class YtDlpMetadataLineTests
{
    private const char Separator = '\u001f';

    private static string MetadataLine(string title, string artist, string duration, string thumbnail) =>
        $"{ApiMetadataLine.Prefix}{title}{Separator}{artist}{Separator}NA{Separator}{duration}{Separator}{thumbnail}";

    [Fact]
    public void Api_parses_all_fields()
    {
        var metadata = ApiMetadataLine.Parse("dQw4w9WgXcQ",
            [MetadataLine("Never Gonna Give You Up", "Rick Astley", "213", "https://i.ytimg.com/vi/x.jpg")]);

        Assert.NotNull(metadata);
        Assert.Equal("Never Gonna Give You Up", metadata.Title);
        Assert.Equal(["Rick Astley"], metadata.Artists!);
        Assert.Equal(213, metadata.DurationSeconds);
        Assert.Equal("https://i.ytimg.com/vi/x.jpg", metadata.ThumbnailUrl);
        Assert.False(metadata.IsEmpty);
    }

    [Fact]
    public void Na_fields_become_null_without_losing_the_title()
    {
        var metadata = ApiMetadataLine.Parse("dQw4w9WgXcQ", [MetadataLine("Some Title", "NA", "NA", "NA")]);

        Assert.NotNull(metadata);
        Assert.Equal("Some Title", metadata.Title);
        Assert.Null(metadata.Artists);
        Assert.Null(metadata.DurationSeconds);
        Assert.Null(metadata.ThumbnailUrl);
    }

    [Fact]
    public void A_title_containing_the_prefix_or_separators_does_not_corrupt_parsing()
    {
        // A separator inside the title would split into too many fields and must be rejected rather
        // than silently shifting every field along by one.
        var metadata = ApiMetadataLine.Parse("dQw4w9WgXcQ",
            [MetadataLine($"weird{Separator}title", "Channel", "10", "NA")]);

        Assert.Null(metadata);
    }

    [Fact]
    public void Missing_metadata_line_is_not_an_error()
    {
        Assert.Null(ApiMetadataLine.Parse("dQw4w9WgXcQ", ["/tmp/harmony/source.webm"]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void File_path_is_found_regardless_of_print_ordering(bool metadataFirst)
    {
        const string path = "/tmp/harmony-resolver-abc/source.webm";
        var metadata = MetadataLine("Title", "Channel", "100", "NA");
        string[] lines = metadataFirst ? [metadata, path] : [path, metadata];

        Assert.Equal(path, ApiMetadataLine.LastNonMetadataLine(lines));
        Assert.Equal(path, DownloaderMetadataLine.LastNonMetadataLine(lines));
    }

    [Fact]
    public void Downloader_and_api_agree_on_the_wire_format()
    {
        Assert.Equal(ApiMetadataLine.Prefix, DownloaderMetadataLine.Prefix);
        Assert.Equal(ApiMetadataLine.PrintTemplate, DownloaderMetadataLine.PrintTemplate);

        var line = MetadataLine("Shared Title", "Shared Channel", "42", "https://example/t.jpg");
        var api = ApiMetadataLine.Parse("dQw4w9WgXcQ", [line]);
        var downloader = DownloaderMetadataLine.Parse([line]);

        Assert.NotNull(api);
        Assert.NotNull(downloader);
        Assert.Equal(api.Title, downloader.Title);
        Assert.Equal(api.Artists, downloader.Artists);
        Assert.Equal(api.DurationSeconds, downloader.DurationSeconds);
        Assert.Equal(api.ThumbnailUrl, downloader.ThumbnailUrl);
    }
}

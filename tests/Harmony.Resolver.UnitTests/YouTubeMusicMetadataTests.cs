using System.Text.Json.Nodes;
using Harmony.Resolver.Downloader;
using Xunit;

namespace Harmony.Resolver.UnitTests;

/// <summary>
/// The YouTube Music <c>next</c> response is the only source of an album browse id, artist ids and
/// square cover art — yt-dlp and the watch page both describe a video. These cover the shapes the
/// parser has to survive, because a miss must degrade to the yt-dlp metadata rather than throw.
/// </summary>
public sealed class YouTubeMusicMetadataTests
{
    private const string VideoId = "abcdefghijk";

    [Fact]
    public void Extracts_album_browse_id_artists_and_square_artwork()
    {
        var song = YouTubeMusicMetadata.ParseWatchResponse(VideoId, WatchResponse(Runs(
            Run("Relja", "UC_relja"),
            Separator(),
            Run("Kamikaza", "MPREb_kamikaza"),
            Separator(),
            Run("2019"))));

        Assert.NotNull(song);
        Assert.Equal("Za Tobom Lud", song["title"]!.GetValue<string>());
        // The album must carry its browse id: "go to album" is gated on it existing.
        Assert.Equal("Kamikaza", song["album"]!["name"]!.GetValue<string>());
        Assert.Equal("MPREb_kamikaza", song["album"]!["id"]!.GetValue<string>());
        var artists = Assert.IsType<JsonArray>(song["artists"]);
        Assert.Single(artists);
        Assert.Equal("Relja", artists[0]!["name"]!.GetValue<string>());
        Assert.Equal("UC_relja", artists[0]!["id"]!.GetValue<string>());
        Assert.Equal("2019", song["year"]!.GetValue<string>());
        Assert.Equal(
            "https://lh3.googleusercontent.com/cover",
            song["thumbnails"]![0]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void Release_detail_links_also_count_as_the_album()
    {
        var song = YouTubeMusicMetadata.ParseWatchResponse(VideoId, WatchResponse(Runs(
            Run("Some Artist", "UC_artist"),
            Separator(),
            Run("Some Album", "FEmusic_release_detail?id=1"))));

        Assert.Equal("Some Album", song!["album"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void A_leading_result_type_label_is_not_an_artist()
    {
        // "Song • Artist • Album" — the label has no navigation endpoint and must be dropped, or it
        // shows up as the performer.
        var song = YouTubeMusicMetadata.ParseWatchResponse(VideoId, WatchResponse(Runs(
            Run("Song"),
            Separator(),
            Run("Relja", "UC_relja"))));

        var artists = Assert.IsType<JsonArray>(song!["artists"]);
        Assert.Single(artists);
        Assert.Equal("Relja", artists[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void An_artist_without_an_endpoint_still_counts()
    {
        var song = YouTubeMusicMetadata.ParseWatchResponse(VideoId, WatchResponse(Runs(
            Run("Unlinked Artist"))));

        var artists = Assert.IsType<JsonArray>(song!["artists"]);
        Assert.Equal("Unlinked Artist", artists[0]!["name"]!.GetValue<string>());
        Assert.Null(artists[0]!["id"]);
    }

    [Fact]
    public void Duration_in_the_byline_becomes_seconds()
    {
        var song = YouTubeMusicMetadata.ParseWatchResponse(VideoId, WatchResponse(Runs(
            Run("Relja", "UC_relja"),
            Separator(),
            Run("3:29"))));

        Assert.Equal(209, song!["duration"]!.GetValue<int>());
        Assert.Equal("3:29", song["length"]!.GetValue<string>());
    }

    [Fact]
    public void A_non_music_response_returns_null_rather_than_throwing()
    {
        // No playlist panel at all: this is what a plain video looks like, and the caller falls
        // back to yt-dlp.
        var response = JsonNode.Parse("""{"contents":{"twoColumnWatchNextResults":{}}}""");

        Assert.Null(YouTubeMusicMetadata.ParseWatchResponse(VideoId, response));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"contents":null}""")]
    [InlineData("""{"contents":{"singleColumnMusicWatchNextResultsRenderer":{"tabbedRenderer":{}}}}""")]
    public void Malformed_responses_return_null(string json)
    {
        Assert.Null(YouTubeMusicMetadata.ParseWatchResponse(VideoId, JsonNode.Parse(json)));
    }

    [Fact]
    public void A_different_video_in_the_panel_is_not_mistaken_for_the_request()
    {
        // The panel is a radio queue seeded from the song, so it holds other tracks too.
        var response = WatchResponse(Runs(Run("Relja", "UC_relja")), videoId: "zzzzzzzzzzz");

        Assert.Null(YouTubeMusicMetadata.ParseWatchResponse(VideoId, response));
    }

    [Fact]
    public void An_unplayable_entry_is_skipped()
    {
        var response = WatchResponse(Runs(Run("Relja", "UC_relja")));
        var renderer = response["contents"]!["singleColumnMusicWatchNextResultsRenderer"]!
            ["tabbedRenderer"]!["watchNextTabbedResultsRenderer"]!["tabs"]![0]!
            ["tabRenderer"]!["content"]!["musicQueueRenderer"]!["content"]!
            ["playlistPanelRenderer"]!["contents"]![0]!["playlistPanelVideoRenderer"]!;
        renderer["unplayableText"] = "Not available";

        Assert.Null(YouTubeMusicMetadata.ParseWatchResponse(VideoId, response));
    }

    [Theory]
    [InlineData("3:29", 209)]
    [InlineData("1:02:03", 3723)]
    [InlineData("0:07", 7)]
    public void Durations_parse(string text, int expected) =>
        Assert.Equal(expected, YouTubeMusicMetadata.ParseDurationSeconds(text));

    [Theory]
    [InlineData("")]
    [InlineData("209")]
    [InlineData("not a duration")]
    [InlineData("1:2:3:4")]
    public void Non_durations_do_not_parse(string text) =>
        Assert.Null(YouTubeMusicMetadata.ParseDurationSeconds(text));

    private static JsonArray Runs(params JsonNode[] runs) => [.. runs];

    private static JsonObject Run(string text, string? browseId = null)
    {
        var run = new JsonObject { ["text"] = text };
        if (browseId is not null)
        {
            run["navigationEndpoint"] = new JsonObject
            {
                ["browseEndpoint"] = new JsonObject { ["browseId"] = browseId }
            };
        }
        return run;
    }

    /// Odd positions in a byline are always separators.
    private static JsonObject Separator() => new() { ["text"] = " • " };

    private static JsonNode WatchResponse(JsonArray bylineRuns, string videoId = VideoId) =>
        new JsonObject
        {
            ["contents"] = new JsonObject
            {
                ["singleColumnMusicWatchNextResultsRenderer"] = new JsonObject
                {
                    ["tabbedRenderer"] = new JsonObject
                    {
                        ["watchNextTabbedResultsRenderer"] = new JsonObject
                        {
                            ["tabs"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["tabRenderer"] = new JsonObject
                                    {
                                        ["content"] = new JsonObject
                                        {
                                            ["musicQueueRenderer"] = new JsonObject
                                            {
                                                ["content"] = new JsonObject
                                                {
                                                    ["playlistPanelRenderer"] = new JsonObject
                                                    {
                                                        ["contents"] = new JsonArray
                                                        {
                                                            new JsonObject
                                                            {
                                                                ["playlistPanelVideoRenderer"] =
                                                                    Renderer(videoId, bylineRuns)
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

    private static JsonObject Renderer(string videoId, JsonArray bylineRuns) => new()
    {
        ["videoId"] = videoId,
        ["title"] = new JsonObject
        {
            ["runs"] = new JsonArray { new JsonObject { ["text"] = "Za Tobom Lud" } }
        },
        ["lengthText"] = new JsonObject
        {
            ["runs"] = new JsonArray { new JsonObject { ["text"] = "3:29" } }
        },
        ["thumbnail"] = new JsonObject
        {
            ["thumbnails"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "https://lh3.googleusercontent.com/cover",
                    ["width"] = 544,
                    ["height"] = 544
                }
            }
        },
        ["longBylineText"] = new JsonObject { ["runs"] = bylineRuns }
    };
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Harmony.Resolver.Downloader;

/// <summary>
/// Builds Harmony-shaped song JSON out of a YouTube Music <c>next</c> response.
///
/// This is a port of the app's rich metadata path (<c>parseWatchTrack</c> and
/// <c>parseSongRuns</c> in <c>nav_parser.dart</c>), which is the only source that yields an album
/// browse id, artist ids and the square Music cover. yt-dlp and the watch page both describe a
/// *video*: channel as artist, letterboxed thumbnail, no album identity at all.
///
/// Every method here is total — a shape that does not match returns null rather than throwing, so
/// an upstream change degrades to the yt-dlp metadata instead of losing metadata entirely.
/// </summary>
public static partial class YouTubeMusicMetadata
{
    /// <summary>
    /// Extracts the track matching <paramref name="videoId"/> from a <c>next</c> response.
    /// Returns null when the response carries no playlist panel, which is what a non-music video
    /// looks like.
    /// </summary>
    public static JsonObject? ParseWatchResponse(string videoId, JsonNode? response)
    {
        var contents = PlaylistPanelContents(response);
        if (contents is null) return null;

        foreach (var entry in contents)
        {
            var renderer = PlaylistPanelVideoRenderer(entry);
            if (renderer is null) continue;
            // A renderer that cannot be played carries no usable metadata either.
            if (renderer["unplayableText"] is not null) continue;
            if (Text(renderer["videoId"]) != videoId) continue;
            return ParseWatchTrack(videoId, renderer);
        }
        return null;
    }

    private static JsonArray? PlaylistPanelContents(JsonNode? response)
    {
        var panel = response
            ?["contents"]?["singleColumnMusicWatchNextResultsRenderer"]?["tabbedRenderer"]
            ?["watchNextTabbedResultsRenderer"]?["tabs"];
        if (panel is not JsonArray tabs) return null;
        foreach (var tab in tabs)
        {
            var contents = tab?["tabRenderer"]?["content"]?["musicQueueRenderer"]?["content"]
                ?["playlistPanelRenderer"]?["contents"];
            if (contents is JsonArray array) return array;
        }
        return null;
    }

    /// <summary>
    /// Unwraps the wrapper renderer used when a track has a video/song counterpart, mirroring the
    /// app's handling of <c>playlistPanelVideoWrapperRenderer</c>.
    /// </summary>
    private static JsonNode? PlaylistPanelVideoRenderer(JsonNode? entry)
    {
        if (entry?["playlistPanelVideoRenderer"] is { } direct) return direct;
        return entry?["playlistPanelVideoWrapperRenderer"]?["primaryRenderer"]
            ?["playlistPanelVideoRenderer"];
    }

    private static JsonObject ParseWatchTrack(string videoId, JsonNode renderer)
    {
        var track = new JsonObject
        {
            ["videoId"] = videoId,
            ["title"] = Text(renderer["title"]?["runs"]?[0]?["text"])
        };

        var length = Text(renderer["lengthText"]?["runs"]?[0]?["text"]);
        if (length is not null) track["length"] = length;

        // The Music cover, not the video still: square art from lh3.googleusercontent.com.
        if (renderer["thumbnail"]?["thumbnails"] is JsonArray thumbnails)
            track["thumbnails"] = CloneArray(thumbnails);

        ApplySongRuns(track, renderer["longBylineText"]?["runs"] as JsonArray);

        if (track["duration"] is null && length is not null)
        {
            var seconds = ParseDurationSeconds(length);
            if (seconds is not null) track["duration"] = seconds;
        }
        return track;
    }

    /// <summary>
    /// Port of <c>parseSongRuns</c>. The byline is a run list where odd indices are separators; an
    /// even run carrying a navigation endpoint is either the album (browse id starting
    /// <c>MPRE</c>, or a release-detail link) or an artist, and bare text is matched against the
    /// duration / year / views shapes before being treated as an artist without an id.
    /// </summary>
    private static void ApplySongRuns(JsonObject track, JsonArray? runs)
    {
        var artists = new JsonArray();
        if (runs is not null)
        {
            for (var index = 0; index < runs.Count; index++)
            {
                if (index % 2 != 0) continue;
                var run = runs[index];
                var text = Text(run?["text"]);
                if (text is null) continue;

                // A leading result-type label ("Song", "Video", …) is not an artist. A real artist
                // in that position carries a navigation endpoint.
                if (index == 0 && run?["navigationEndpoint"] is null && IsResultTypeLabel(text))
                    continue;

                if (run?["navigationEndpoint"] is { } endpoint)
                {
                    var browseId = Text(endpoint["browseEndpoint"]?["browseId"]);
                    var item = new JsonObject { ["name"] = text, ["id"] = browseId };
                    if (browseId is not null
                        && (browseId.StartsWith("MPRE", StringComparison.Ordinal)
                            || browseId.Contains("release_detail", StringComparison.Ordinal)))
                        track["album"] = item;
                    else
                        artists.Add(item);
                    continue;
                }

                if (ViewsPattern().IsMatch(text) && index > 0)
                {
                    track["views"] = text.Split(' ')[0];
                }
                else if (DurationPattern().IsMatch(text))
                {
                    track["length"] = text;
                    var seconds = ParseDurationSeconds(text);
                    if (seconds is not null) track["duration"] = seconds;
                }
                else if (YearPattern().IsMatch(text))
                {
                    track["year"] = text;
                }
                else
                {
                    artists.Add(new JsonObject { ["name"] = text, ["id"] = null });
                }
            }
        }
        track["artists"] = artists;
    }

    private static readonly HashSet<string> ResultTypeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "song", "video", "album", "single", "ep", "playlist", "artist", "station"
    };

    private static bool IsResultTypeLabel(string text) => ResultTypeLabels.Contains(text.Trim());

    /// <summary>"3:29" and "1:02:03" both parse; anything else returns null.</summary>
    public static int? ParseDurationSeconds(string value)
    {
        var parts = value.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        var total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var component) || component < 0) return null;
            total = total * 60 + component;
        }
        return total;
    }

    private static JsonArray CloneArray(JsonArray source)
    {
        var clone = new JsonArray();
        foreach (var item in source) clone.Add(item?.DeepClone());
        return clone;
    }

    private static string? Text(JsonNode? node)
    {
        if (node is null) return null;
        if (node.GetValueKind() != JsonValueKind.String) return null;
        var value = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [GeneratedRegex(@"^\d([^ ])* [^ ]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ViewsPattern();

    [GeneratedRegex(@"^(\d+:)*\d+:\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"^\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();
}

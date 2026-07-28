using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Harmony.Resolver.Downloader;

/// <summary>
/// Reads display metadata from YouTube Music's <c>next</c> endpoint — the same call the app makes
/// when it opens an album, and the only one that returns an album browse id, artist ids and square
/// cover art.
///
/// Runs here rather than on the API because Delegated mode deliberately keeps all upstream traffic
/// on the fleet's residential addresses.
///
/// Best effort throughout: any failure returns null and the caller falls back to yt-dlp, so a shape
/// change upstream costs richness, never coverage.
/// </summary>
public sealed class YouTubeMusicMetadataClient(
    HttpClient http,
    ILogger<YouTubeMusicMetadataClient> logger)
{
    private const string NextUrl =
        "https://music.youtube.com/youtubei/v1/next?prettyPrint=false&alt=json";

    /// The public web client YouTube Music itself uses; mirrors the app's context exactly.
    private static readonly JsonObject Context = new()
    {
        ["client"] = new JsonObject
        {
            ["clientName"] = "WEB_REMIX",
            ["clientVersion"] = "1.20230213.01.00"
        },
        ["user"] = new JsonObject()
    };

    public async Task<JsonObject?> FetchSongAsync(string videoId, CancellationToken cancellationToken)
    {
        try
        {
            var body = new JsonObject
            {
                ["context"] = Context.DeepClone(),
                ["videoId"] = videoId,
                // A radio playlist seeded from the song: this is what makes the response carry the
                // playlist panel the metadata is read from.
                ["playlistId"] = $"RDAMVM{videoId}",
                ["enablePersistentPlaylistPanel"] = true,
                ["isAudioOnly"] = true,
                ["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL",
                ["watchEndpointMusicSupportedConfigs"] = new JsonObject
                {
                    ["watchEndpointMusicConfig"] = new JsonObject
                    {
                        ["hasPersistentPlaylistPanel"] = true,
                        ["musicVideoType"] = "MUSIC_VIDEO_TYPE_ATV"
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, NextUrl)
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.TryAddWithoutValidation("origin", "https://music.youtube.com");
            request.Headers.TryAddWithoutValidation("cookie", "CONSENT=YES+1");

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "YouTube Music metadata for {VideoId} answered {Status}.", videoId, (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken);
            var song = YouTubeMusicMetadata.ParseWatchResponse(videoId, payload);
            if (song is null)
                logger.LogDebug("No music metadata in the response for {VideoId}.", videoId);
            return song;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "YouTube Music metadata lookup failed for {VideoId}.", videoId);
            return null;
        }
    }

    /// <summary>
    /// Merges the rich document over yt-dlp's flat view. The flat fields stay populated because
    /// the API gates on <c>Title</c> and diagnostics read them; the document is what carries the
    /// album and artist identity.
    /// </summary>
    public static DownloadedMetadata Combine(JsonObject song, DownloadedMetadata? fallback)
    {
        var title = StringValue(song["title"]) ?? fallback?.Title ?? string.Empty;
        var artists = song["artists"] is JsonArray list
            ? list.Select(item => StringValue(item?["name"])).OfType<string>().ToList()
            : null;
        var album = StringValue(song["album"]?["name"]) ?? fallback?.Album;
        var duration = song["duration"]?.GetValueKind() == JsonValueKind.Number
            ? song["duration"]!.GetValue<int>()
            : fallback?.DurationSeconds;
        var thumbnail = song["thumbnails"] is JsonArray thumbnails && thumbnails.Count > 0
            ? StringValue(thumbnails[^1]?["url"]) ?? fallback?.ThumbnailUrl
            : fallback?.ThumbnailUrl;

        return new DownloadedMetadata(
            title,
            artists is { Count: > 0 } ? artists : fallback?.Artists,
            album,
            duration,
            thumbnail,
            song.ToJsonString());
    }

    private static string? StringValue(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
}

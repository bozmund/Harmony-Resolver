namespace Harmony.Resolver.Downloader;

/// <summary>
/// The downloader's copy of the yt-dlp metadata line format. Kept in sync by hand with
/// <c>Harmony.Resolver.Api.Infrastructure.Extraction.YtDlpMetadataLine</c>; the two projects share
/// no assembly, and the format is small and covered by tests on both sides.
/// </summary>
public static class YtDlpMetadataLine
{
    public const string Prefix = "harmony-meta:";
    /// ASCII unit separator: cannot occur in a title, so no escaping is needed.
    private const string Separator = "\u001f";

    public const string PrintTemplate =
        $"{Prefix}%(title)s{Separator}%(channel)s{Separator}%(duration)s{Separator}%(thumbnail)s";

    /// The last line that is not our metadata line — i.e. the downloaded file path.
    public static string? LastNonMetadataLine(IEnumerable<string> lines) => lines
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith(Prefix, StringComparison.Ordinal))
        .LastOrDefault();

    public static DownloadedMetadata? Parse(IEnumerable<string> lines)
    {
        var line = lines.Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null) return null;

        var fields = line[Prefix.Length..].Split(Separator);
        if (fields.Length != 4) return null;
        var title = Value(fields[0]);
        if (title is null) return null;
        var channel = Value(fields[1]);
        return new DownloadedMetadata(
            title,
            channel is null ? null : [channel],
            DurationSeconds: int.TryParse(Value(fields[2]), out var seconds) ? seconds : null,
            ThumbnailUrl: Value(fields[3]));
    }

    /// yt-dlp prints the literal "NA" for fields it could not determine.
    private static string? Value(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Length == 0 || trimmed == "NA" ? null : trimmed;
    }
}

public sealed record DownloadedMetadata(
    string Title,
    IReadOnlyList<string>? Artists = null,
    int? DurationSeconds = null,
    string? ThumbnailUrl = null);

public sealed record DownloadedMedia(string Path, DownloadedMetadata? Metadata = null);

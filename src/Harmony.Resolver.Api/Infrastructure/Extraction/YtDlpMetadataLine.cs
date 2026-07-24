using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

/// <summary>
/// Encodes and parses the single extra <c>--print</c> line yt-dlp emits for display metadata.
/// The line carries a fixed prefix so it can be told apart from the <c>after_move:filepath</c>
/// line without depending on yt-dlp's output ordering.
/// </summary>
public static class YtDlpMetadataLine
{
    public const string Prefix = "harmony-meta:";
    /// ASCII unit separator: cannot occur in a title, so no escaping is needed.
    private const string Separator = "\u001f";

    /// yt-dlp output template.
    public const string PrintTemplate =
        $"{Prefix}%(title)s{Separator}%(channel)s{Separator}%(duration)s{Separator}%(thumbnail)s";

    /// The last line that is not our metadata line — i.e. the extracted file path.
    public static string? LastNonMetadataLine(IEnumerable<string> lines) => lines
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith(Prefix, StringComparison.Ordinal))
        .LastOrDefault();

    public static TrackMetadata? Parse(string videoId, IEnumerable<string> lines)
    {
        var line = lines.Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith(Prefix, StringComparison.Ordinal));
        if (line is null) return null;

        var fields = line[Prefix.Length..].Split(Separator);
        if (fields.Length != 4) return null;
        var title = Value(fields[0]);
        if (title is null) return null;
        var channel = Value(fields[1]);
        return new TrackMetadata(
            videoId,
            title,
            channel is null ? null : [channel],
            Album: null,
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

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Harmony.Resolver.Downloader;

/// <summary>
/// A safe, structured downloader failure. Only <see cref="Code"/> is sent to Resolver; the
/// remaining fields are suitable for local structured logs and deliberately exclude stream URLs,
/// credentials and request query strings.
/// </summary>
public sealed class DownloadException : Exception
{
    public DownloadException(string code, string detail, string? stage = null, string? tool = null, int? exitCode = null)
        : base($"{code}: {DownloadFailure.SanitizeDetail(detail)}")
    {
        Code = code;
        Detail = DownloadFailure.SanitizeDetail(detail);
        Stage = stage;
        Tool = tool;
        ExitCode = exitCode;
    }

    public string Code { get; }
    public string Detail { get; }
    public string? Stage { get; }
    public string? Tool { get; }
    public int? ExitCode { get; }
}

internal static partial class DownloadFailure
{
    [GeneratedRegex("https?://[^\\s\\]\\)\\\"']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    internal static string SanitizeDetail(string value)
    {
        var sanitized = UrlPattern().Replace(value, "[redacted-url]");
        sanitized = sanitized.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 500 ? sanitized : sanitized[..500];
    }

    internal static string ProcessFailureCode(string stage, string tool, string stderr)
    {
        var toolName = Path.GetFileNameWithoutExtension(tool).ToLowerInvariant();
        var error = stderr.ToLowerInvariant();
        if (toolName == "ffmpeg")
        {
            if (error.Contains("403") || error.Contains("forbidden") || error.Contains("access denied"))
                return "source_fingerprint_ffmpeg_access_denied";
            if (error.Contains("401") || error.Contains("unauthorized"))
                return "source_fingerprint_ffmpeg_unauthorized";
            if (error.Contains("timed out") || error.Contains("connection refused") || error.Contains("network is unreachable"))
                return "source_fingerprint_ffmpeg_network_failed";
            return "source_fingerprint_ffmpeg_failed";
        }

        if (toolName == "fpcalc") return "source_fingerprint_fpcalc_failed";
        if (toolName is "yt-dlp" or "yt_dlp") return "source_inspection_yt_dlp_failed";
        return "source_fingerprint_process_failed";
    }
}

/// <summary>
/// Runs yt-dlp to fetch a video's best audio to a local file. Because the agent runs on a residential
/// IP, yt-dlp's bot gate passes where the datacenter-hosted resolver's does not. The raw file is
/// uploaded as-is; the resolver normalizes it to Ogg Opus server-side.
/// </summary>
public sealed partial class YtDlpDownloader(DownloaderOptions options, ILogger<YtDlpDownloader> logger)
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    public async Task<string> DownloadAsync(string videoId, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!VideoIdPattern().IsMatch(videoId))
            throw new DownloadException("invalid_video_id", "video id is not a valid 11-character YouTube id");

        var outputTemplate = Path.Combine(workingDirectory, "source.%(ext)s");
        var startInfo = new ProcessStartInfo
        {
            FileName = options.YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in BuildArguments(videoId, outputTemplate, options.MaxDurationMinutes))
            startInfo.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.DownloadTimeoutSeconds));

        using var process = Process.Start(startInfo)
            ?? throw new DownloadException("downloader_start_failed", "yt-dlp failed to start");
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new DownloadException("download_timeout", "yt-dlp exceeded the download timeout");
        }

        var output = (await outputTask).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        var error = await errorTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new DownloadException("yt_dlp_failed", error, stage: "download", tool: "yt-dlp", exitCode: process.ExitCode);

        var path = Path.GetFullPath(output);
        var root = Path.GetFullPath(workingDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
            throw new DownloadException("unsafe_output", "yt-dlp produced an unexpected output path");

        logger.LogDebug("yt-dlp produced {Path} for {VideoId}.", path, videoId);
        return path;
    }

    internal static string[] BuildArguments(
        string videoId, string outputTemplate, int maxDurationMinutes) =>
    [
        "--no-playlist", "--no-progress", "--no-warnings",
        "--match-filter", $"duration <= {maxDurationMinutes * 60}",
        "-f", "bestaudio/best",
        "-o", outputTemplate, "--print", "after_move:filepath",
        $"https://www.youtube.com/watch?v={videoId}"
    ];

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

}

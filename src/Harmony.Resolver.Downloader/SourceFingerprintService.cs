using System.Diagnostics;
using System.Globalization;

namespace Harmony.Resolver.Downloader;

public sealed record SourceFingerprint(
    double DurationSeconds,
    string FingerprintA,
    string FingerprintB);

public sealed class SourceFingerprintService(DownloaderOptions options)
{
    public async Task<SourceFingerprint> ComputeAsync(
        string videoId,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var (duration, streamUrl) = await InspectAsync(videoId, cancellationToken);
        var length = Math.Min(15d, duration);
        var firstStart = Math.Clamp(duration * 0.20, 0, Math.Max(0, duration - length));
        var secondStart = Math.Clamp(duration * 0.60, 0, Math.Max(0, duration - length));
        var first = await FingerprintRemoteSegmentAsync(
            streamUrl, workingDirectory, "a", firstStart, length, cancellationToken);
        var second = await FingerprintRemoteSegmentAsync(
            streamUrl, workingDirectory, "b", secondStart, length, cancellationToken);
        return new SourceFingerprint(duration, first, second);
    }

    private async Task<(double Duration, string Url)> InspectAsync(
        string videoId,
        CancellationToken cancellationToken)
    {
        var info = CreateProcess(options.YtDlpPath,
            "--no-playlist", "--no-progress", "--no-warnings",
            "--match-filter", $"duration <= {options.MaxDurationMinutes * 60}",
            "-f", "worstaudio/worst", "--print", "duration", "--print", "urls",
            $"https://www.youtube.com/watch?v={videoId}");
        var output = await RunAsync(info, cancellationToken, "source_inspection");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2
            || !double.TryParse(lines[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            || duration <= 0
            || !Uri.TryCreate(lines[^1], UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new DownloadException("source_inspection_invalid_metadata", "yt-dlp returned invalid source metadata", stage: "source_inspection", tool: "yt-dlp");
        return (duration, uri.AbsoluteUri);
    }

    private static async Task<string> FingerprintRemoteSegmentAsync(
        string streamUrl,
        string workingDirectory,
        string suffix,
        double start,
        double length,
        CancellationToken cancellationToken)
    {
        var wave = Path.Combine(workingDirectory, $"source-{suffix}.wav");
        await RunAsync(CreateProcess("ffmpeg",
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", start.ToString(CultureInfo.InvariantCulture),
            "-t", length.ToString(CultureInfo.InvariantCulture),
            "-i", streamUrl, "-vn", "-ac", "1", "-ar", "11025", "-y", wave), cancellationToken,
            $"source_fingerprint_{suffix}");
        var output = await RunAsync(CreateProcess(
            "fpcalc", "-raw", "-length", Math.Ceiling(length).ToString(CultureInfo.InvariantCulture), wave),
            cancellationToken, $"source_fingerprint_{suffix}");
        var fingerprint = output.Split('\n')
            .FirstOrDefault(line => line.StartsWith("FINGERPRINT=", StringComparison.Ordinal))
            ?["FINGERPRINT=".Length..].Trim();
        return string.IsNullOrWhiteSpace(fingerprint)
            ? throw new DownloadException("source_fingerprint_empty", "fpcalc returned no fingerprint", stage: $"source_fingerprint_{suffix}", tool: "fpcalc")
            : fingerprint;
    }

    private static ProcessStartInfo CreateProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task<string> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        string stage)
    {
        using var process = Process.Start(startInfo)
            ?? throw new DownloadException("source_fingerprint_process_start_failed", startInfo.FileName, stage: stage, tool: startInfo.FileName);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new DownloadException(
                DownloadFailure.ProcessFailureCode(stage, startInfo.FileName, error), error,
                stage: stage, tool: startInfo.FileName, exitCode: process.ExitCode);
        return output.Trim();
    }
}

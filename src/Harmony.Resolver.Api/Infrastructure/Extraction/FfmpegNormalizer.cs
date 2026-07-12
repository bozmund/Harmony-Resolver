using System.Diagnostics;
using Harmony.Resolver.Api.Configuration;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

public sealed class FfmpegNormalizer(ResolverOptions options)
{
    public async Task<byte[]> NormalizeAsync(string inputPath, string workingDirectory, CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(workingDirectory, "normalized.ogg");
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-i", inputPath,
            "-t", TimeSpan.FromMinutes(options.MaxDurationMinutes).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-vn", "-c:a", "libopus", "-b:a", "128k", "-f", "ogg", "-y", outputPath
        }) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new ExtractionException("ffmpeg_start_failed", "ffmpeg");
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new ExtractionException("ffmpeg_failed", "ffmpeg", new InvalidDataException(Sanitize(error)));

        var info = new FileInfo(outputPath);
        if (!info.Exists || info.Length == 0) throw new ExtractionException("ffmpeg_empty_output", "ffmpeg");
        if (info.Length > options.MaxObjectMiB * 1024L * 1024L)
            throw new ExtractionException("object_too_large", "ffmpeg");
        return await File.ReadAllBytesAsync(outputPath, cancellationToken);
    }

    private static string Sanitize(string value) => value.Length <= 500 ? value : value[..500];
}

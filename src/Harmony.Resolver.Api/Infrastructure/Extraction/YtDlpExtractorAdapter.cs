using System.Diagnostics;
using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;

using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

public sealed class YtDlpExtractorAdapter(ResolverOptions options, FfmpegNormalizer normalizer) : IExtractorAdapter
{
    public string Name => "yt-dlp";

    public async Task<ExtractedAudio> ExtractAsync(string videoId, CancellationToken cancellationToken)
    {
        var workingDirectory = Directory.CreateTempSubdirectory("harmony-resolver-").FullName;
        try
        {
            var outputTemplate = Path.Combine(workingDirectory, "source.%(ext)s");
            var startInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in BuildArguments(videoId, outputTemplate, options.MaxDurationMinutes, options.MaxObjectMiB))
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo) ?? throw new ExtractionException("extractor_start_failed", Name);
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var lines = (await outputTask).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // The metadata line is self-identifying rather than positional: yt-dlp gives no ordering
            // guarantee between --print stages, and the previous LastOrDefault() parse silently
            // breaks the moment a second --print is added.
            var output = YtDlpMetadataLine.LastNonMetadataLine(lines);
            var error = await errorTask;
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                throw new ExtractionException("yt_dlp_failed", Name, new InvalidDataException(Sanitize(error)));

            var inputPath = Path.GetFullPath(output);
            var root = Path.GetFullPath(workingDirectory) + Path.DirectorySeparatorChar;
            if (!inputPath.StartsWith(root, StringComparison.Ordinal) || !File.Exists(inputPath))
                throw new ExtractionException("unsafe_extractor_output", Name);
            var audio = await normalizer.NormalizeAsync(inputPath, workingDirectory, cancellationToken);
            return new ExtractedAudio(audio, YtDlpMetadataLine.Parse(videoId, lines));
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    internal static string[] BuildArguments(
        string videoId, string outputTemplate, int maxDurationMinutes, int maxObjectMiB) =>
    [
        "--no-playlist", "--no-progress", "--no-warnings",
        "--match-filter", $"duration <= {maxDurationMinutes * 60}",
        "--max-filesize", $"{maxObjectMiB}M", "-f", "bestaudio/best",
        "-o", outputTemplate,
        "--print", YtDlpMetadataLine.PrintTemplate,
        "--print", "after_move:filepath",
        $"https://www.youtube.com/watch?v={videoId}"
    ];

    private static string Sanitize(string value) => value.Length <= 500 ? value : value[..500];
}

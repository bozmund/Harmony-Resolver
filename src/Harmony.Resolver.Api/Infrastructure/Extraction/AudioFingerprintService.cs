using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

public sealed record AudioFingerprint(double DurationSeconds, string SegmentA, string SegmentB);

public sealed class AudioFingerprintService
{
    public async Task<AudioFingerprint> ComputeAsync(
        string inputPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var duration = await ProbeDurationAsync(inputPath, cancellationToken);
        if (duration <= 0) throw new ExtractionException("invalid_duration", "ffprobe");
        var segmentLength = Math.Min(15d, duration);
        var firstStart = Math.Clamp(duration * 0.20, 0, Math.Max(0, duration - segmentLength));
        var secondStart = Math.Clamp(duration * 0.60, 0, Math.Max(0, duration - segmentLength));
        var first = await FingerprintSegmentAsync(
            inputPath, workingDirectory, "a", firstStart, segmentLength, cancellationToken);
        var second = await FingerprintSegmentAsync(
            inputPath, workingDirectory, "b", secondStart, segmentLength, cancellationToken);
        return new AudioFingerprint(duration, first, second);
    }

    public static bool Matches(AudioFingerprint uploaded, AudioFingerprint source)
    {
        var tolerance = Math.Max(2d, source.DurationSeconds * 0.01);
        return Math.Abs(uploaded.DurationSeconds - source.DurationSeconds) <= tolerance
            && Similarity(uploaded.SegmentA, source.SegmentA) >= 0.85
            && Similarity(uploaded.SegmentB, source.SegmentB) >= 0.85;
    }

    internal static double Similarity(string left, string right)
    {
        var leftValues = ParseFingerprint(left);
        var rightValues = ParseFingerprint(right);
        var count = Math.Min(leftValues.Length, rightValues.Length);
        if (count == 0) return 0;
        long differentBits = 0;
        for (var index = 0; index < count; index++)
            differentBits += BitOperations.PopCount(leftValues[index] ^ rightValues[index]);
        return 1d - differentBits / (count * 32d);
    }

    private static uint[] ParseFingerprint(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => unchecked((uint)long.Parse(item, CultureInfo.InvariantCulture)))
            .ToArray();

    private static async Task<double> ProbeDurationAsync(
        string inputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateProcess("ffprobe",
            "-v", "error", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", inputPath);
        var output = await RunAsync(startInfo, cancellationToken);
        return double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : throw new ExtractionException("invalid_duration", "ffprobe");
    }

    private static async Task<string> FingerprintSegmentAsync(
        string inputPath,
        string workingDirectory,
        string suffix,
        double start,
        double length,
        CancellationToken cancellationToken)
    {
        var wavePath = Path.Combine(workingDirectory, $"fingerprint-{suffix}.wav");
        var ffmpeg = CreateProcess("ffmpeg",
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", start.ToString(CultureInfo.InvariantCulture),
            "-t", length.ToString(CultureInfo.InvariantCulture),
            "-i", inputPath, "-vn", "-ac", "1", "-ar", "11025", "-y", wavePath);
        await RunAsync(ffmpeg, cancellationToken);
        var output = await RunAsync(CreateProcess(
            "fpcalc", "-raw", "-length", Math.Ceiling(length).ToString(CultureInfo.InvariantCulture), wavePath),
            cancellationToken);
        var fingerprint = output.Split('\n')
            .FirstOrDefault(line => line.StartsWith("FINGERPRINT=", StringComparison.Ordinal))
            ?["FINGERPRINT=".Length..].Trim();
        return string.IsNullOrWhiteSpace(fingerprint)
            ? throw new ExtractionException("fingerprint_failed", "fpcalc")
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
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new ExtractionException("fingerprint_process_failed", startInfo.FileName);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new ExtractionException("fingerprint_process_failed", startInfo.FileName,
                new InvalidDataException(error.Length <= 500 ? error : error[..500]));
        return output.Trim();
    }
}

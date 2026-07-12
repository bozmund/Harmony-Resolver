using Harmony.Resolver.Api.Abstractions;
using Harmony.Resolver.Api.Configuration;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace Harmony.Resolver.Api.Infrastructure.Extraction;

public sealed class YoutubeExplodeExtractorAdapter(
    YoutubeClient youtube, ResolverOptions options, FfmpegNormalizer normalizer) : IExtractorAdapter
{
    public string Name => "youtube-explode";

    public async Task<byte[]> ExtractAsync(string videoId, CancellationToken cancellationToken)
    {
        var workingDirectory = Directory.CreateTempSubdirectory("harmony-resolver-").FullName;
        try
        {
            var video = await youtube.Videos.GetAsync(videoId, cancellationToken);
            if (video.Duration is null || video.Duration > TimeSpan.FromMinutes(options.MaxDurationMinutes))
                throw new ExtractionException("duration_limit", Name);
            var manifest = await youtube.Videos.Streams.GetManifestAsync(videoId, cancellationToken);
            var stream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate()
                ?? throw new ExtractionException("no_audio_stream", Name);
            if (stream.Size.Bytes > options.MaxObjectMiB * 1024L * 1024L)
                throw new ExtractionException("object_too_large", Name);
            var inputPath = Path.Combine(workingDirectory, "source." + stream.Container.Name);
            await youtube.Videos.Streams.DownloadAsync(stream, inputPath, cancellationToken: cancellationToken);
            return await normalizer.NormalizeAsync(inputPath, workingDirectory, cancellationToken);
        }
        catch (ExtractionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ExtractionException("youtube_explode_failed", Name, exception);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }
}

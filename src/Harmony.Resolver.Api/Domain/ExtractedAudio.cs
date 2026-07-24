namespace Harmony.Resolver.Api.Domain;

/// <summary>
/// Audio bytes plus whatever display metadata the extractor happened to learn on the way. Metadata
/// is optional: extractors that cannot cheaply produce it return audio alone rather than paying for
/// a second upstream round trip.
/// </summary>
public sealed record ExtractedAudio(byte[] Audio, TrackMetadata? Metadata = null)
{
    public static implicit operator ExtractedAudio(byte[] audio) => new(audio);
}

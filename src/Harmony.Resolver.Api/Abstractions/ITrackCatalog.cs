using Harmony.Resolver.Api.Domain;

namespace Harmony.Resolver.Api.Abstractions;

public interface ITrackCatalog
{
    TrackInfo Get(string videoId);
    bool TryBegin(string videoId);
    void Ready(string videoId, byte[] audio);
    void Failed(string videoId, string code);
    byte[]? Read(string videoId);
    IReadOnlyCollection<TrackInfo> Snapshot();
}

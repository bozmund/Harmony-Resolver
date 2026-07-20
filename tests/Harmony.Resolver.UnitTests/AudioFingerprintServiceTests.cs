using Harmony.Resolver.Api.Infrastructure.Extraction;
using Xunit;

namespace Harmony.Resolver.UnitTests;

public sealed class AudioFingerprintServiceTests
{
    [Fact]
    public void Matching_fingerprints_and_nearby_duration_are_accepted()
    {
        var uploaded = new AudioFingerprint(240, "1,2,3,4", "5,6,7,8");
        var source = new AudioFingerprint(241.5, "1,2,3,4", "5,6,7,8");

        Assert.True(AudioFingerprintService.Matches(uploaded, source));
    }

    [Fact]
    public void Wrong_audio_is_rejected_even_when_duration_matches()
    {
        var uploaded = new AudioFingerprint(240, "0,0,0,0", "0,0,0,0");
        var source = new AudioFingerprint(
            240, "4294967295,4294967295,4294967295,4294967295",
            "4294967295,4294967295,4294967295,4294967295");

        Assert.False(AudioFingerprintService.Matches(uploaded, source));
    }

    [Fact]
    public void Excessive_duration_difference_is_rejected()
    {
        var uploaded = new AudioFingerprint(240, "1,2,3,4", "5,6,7,8");
        var source = new AudioFingerprint(243, "1,2,3,4", "5,6,7,8");

        Assert.False(AudioFingerprintService.Matches(uploaded, source));
    }
}

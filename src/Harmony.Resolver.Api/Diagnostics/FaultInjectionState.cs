namespace Harmony.Resolver.Api.Diagnostics;

public sealed class FaultInjectionState
{
    private string? _profile;
    public string? Profile => Volatile.Read(ref _profile);
    public void Set(string? profile) => Volatile.Write(ref _profile, profile);
}

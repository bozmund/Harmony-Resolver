using System.Text.RegularExpressions;

namespace Harmony.Resolver.Api.Domain;

public static partial class VideoIds
{
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsValid(string value) => Pattern().IsMatch(value);
}

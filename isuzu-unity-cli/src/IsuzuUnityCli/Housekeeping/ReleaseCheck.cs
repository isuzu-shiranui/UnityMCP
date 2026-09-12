using System.Globalization;
using System.Text.Json;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Housekeeping;

/// <summary>
/// Whether a newer release exists. Never installs one.
/// </summary>
/// <remarks>
/// Knowing costs nothing; installing is the part that can break a machine mid-session, so it
/// stays a decision. The answer is cached because this runs from a report someone reads often,
/// and a network round trip on every run would make that report something they stop running.
/// </remarks>
public static class ReleaseCheck
{
    public const string LatestUrl = "https://api.github.com/repos/isuzu-shiranui/UnityMCP/releases/latest";

    /// <summary>How long an answer stands before it is worth asking again.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    /// <summary>Where the answer is kept, beside the other state this tool writes.</summary>
    public static string CachePath =>
        Path.Combine(StatePaths.PrimaryRoot(), "cache", "latest-release.json");

    public sealed class Cached
    {
        public string Tag { get; init; } = "";
        public DateTimeOffset CheckedAt { get; init; }
    }

    /// <summary>The newest release tag, from the cache when it is still fresh.</summary>
    /// <param name="fetch">
    /// How to reach GitHub. Passed in so a test never touches the network, and so a caller that
    /// must not block can hand in something that gives up quickly.
    /// </param>
    /// <param name="cachePath">
    /// Where the answer is kept. Passed in so a test is not answered by whatever this machine
    /// happened to cache: a fresh entry short-circuits before <paramref name="fetch"/> is
    /// reached, so a test that leaves this to the real path passes on a machine that has never
    /// run the tool and fails on one that has.
    /// </param>
    public static async Task<string?> LatestTag(
        Func<CancellationToken, Task<string>> fetch,
        CancellationToken cancellation,
        string? cachePath = null)
    {
        var path = cachePath ?? CachePath;
        var cached = ReadCache(path);

        if (cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < CacheFor)
        {
            return cached.Tag.Length > 0 ? cached.Tag : null;
        }

        string tag;

        try
        {
            tag = TagFrom(await fetch(cancellation));
        }
        catch (Exception)
        {
            // Offline, rate limited, or GitHub having a bad day. A version check is not worth
            // failing anything over, and the empty answer is cached so it is not asked again on
            // the next run of a report someone may be running in a loop.
            WriteCache(path, new Cached { Tag = "", CheckedAt = DateTimeOffset.UtcNow });
            return null;
        }

        WriteCache(path, new Cached { Tag = tag, CheckedAt = DateTimeOffset.UtcNow });
        return tag.Length > 0 ? tag : null;
    }

    /// <summary>Asks GitHub. The one place that does.</summary>
    /// <remarks>
    /// GitHub refuses a request with no User-Agent, and the refusal arrives as a 403 that reads
    /// like a permission problem.
    /// </remarks>
    public static async Task<string> FromGitHub(CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        http.DefaultRequestHeaders.UserAgent.ParseAdd("isuzu-unity-cli/" + Program.Version());

        return await http.GetStringAsync(LatestUrl, cancellation);
    }

    /// <summary>The newest tag this machine already learned about, without asking again.</summary>
    /// <remarks>
    /// Separate from <see cref="LatestTag"/> because the two do not cost the same: this is one
    /// file read, and that one can sit on the network for as long as its fetch allows. Every
    /// command can afford this; only the two whose job is to look can afford the other.
    /// <para>
    /// A stale entry is still used. It cannot name a release that does not exist, and going
    /// quiet about one that does helps nobody.
    /// </para>
    /// </remarks>
    public static string? KnownTag(string? cachePath = null)
    {
        var cached = ReadCache(cachePath ?? CachePath);

        return cached is not null && cached.Tag.Length > 0 ? cached.Tag : null;
    }

    /// <summary>The tag out of GitHub's release JSON.</summary>
    public static string TagFrom(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement.TryGetProperty("tag_name", out var tag)
            ? tag.GetString() ?? ""
            : "";
    }

    /// <summary>Whether <paramref name="tag"/> is a release newer than what is running.</summary>
    /// <remarks>
    /// Ordered as Semantic Versioning orders them. The core is compared field by field as numbers,
    /// since a string comparison puts 4.10.0 before 4.9.0. A prerelease comes before the release of
    /// the same core, and build metadata takes no part.
    /// </remarks>
    public static bool IsNewer(string tag, string current)
    {
        return ParseVersion(tag) is { } released
            && ParseVersion(current) is { } running
            && Compare(released, running) > 0;
    }

    private readonly record struct SemanticVersion(int[] Core, string[] Prerelease);

    private static SemanticVersion? ParseVersion(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');
        var plus = text.IndexOf('+');

        if (plus >= 0)
        {
            text = text[..plus];
        }

        var dash = text.IndexOf('-');
        var core = dash >= 0 ? text[..dash] : text;
        var prerelease = dash >= 0 ? text[(dash + 1)..].Split('.') : [];
        var numbers = new List<int>();

        foreach (var piece in core.Split('.'))
        {
            if (!int.TryParse(piece, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                break;
            }

            numbers.Add(value);
        }

        return numbers.Count == 0 ? null : new SemanticVersion(numbers.ToArray(), prerelease);
    }

    private static int Compare(SemanticVersion a, SemanticVersion b)
    {
        for (var i = 0; i < Math.Max(a.Core.Length, b.Core.Length); i++)
        {
            var x = i < a.Core.Length ? a.Core[i] : 0;
            var y = i < b.Core.Length ? b.Core[i] : 0;

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        if (a.Prerelease.Length == 0 || b.Prerelease.Length == 0)
        {
            return b.Prerelease.Length.CompareTo(a.Prerelease.Length);
        }

        for (var i = 0; i < Math.Min(a.Prerelease.Length, b.Prerelease.Length); i++)
        {
            var order = CompareIdentifier(a.Prerelease[i], b.Prerelease[i]);

            if (order != 0)
            {
                return order;
            }
        }

        return a.Prerelease.Length.CompareTo(b.Prerelease.Length);
    }

    /// <summary>Numeric identifiers compare as numbers and come before alphanumeric ones.</summary>
    private static int CompareIdentifier(string a, string b)
    {
        var style = System.Globalization.NumberStyles.None;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var aNumeric = long.TryParse(a, style, culture, out var x);
        var bNumeric = long.TryParse(b, style, culture, out var y);

        return (aNumeric, bNumeric) switch
        {
            (true, true) => x.CompareTo(y),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(a, b),
        };
    }

    /// <summary>
    /// Two lines: the tag, then when it was fetched.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than through JsonSerializer, which needs reflection this binary is
    /// published without: the reflection-based overloads compile in Debug and fail the AOT
    /// publish with IL3050. Two fields do not earn a source-generated context.
    /// </remarks>
    private static Cached? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllLines(path);

            if (lines.Length < 2 || !DateTimeOffset.TryParse(
                    lines[1], null, DateTimeStyles.RoundtripKind, out var checkedAt))
            {
                return null;
            }

            return new Cached { Tag = lines[0], CheckedAt = checkedAt };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteCache(string path, Cached value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, new[] { value.Tag, value.CheckedAt.ToString("o") });
        }
        catch (Exception)
        {
            // A cache that cannot be written costs a round trip next time and nothing else.
        }
    }
}

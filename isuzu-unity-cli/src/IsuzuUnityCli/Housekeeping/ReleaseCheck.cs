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
    /// Compared field by field as numbers. A string comparison puts 4.10.0 before 4.9.0, which is
    /// how a newer release ends up reported as an older one.
    /// </remarks>
    public static bool IsNewer(string tag, string current)
    {
        var released = Parts(tag);
        var running = Parts(current);

        if (released.Length == 0 || running.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < Math.Max(released.Length, running.Length); i++)
        {
            var a = i < released.Length ? released[i] : 0;
            var b = i < running.Length ? running[i] : 0;

            if (a != b)
            {
                return a > b;
            }
        }

        return false;
    }

    private static int[] Parts(string version)
    {
        var text = version.TrimStart('v', 'V');
        var pieces = text.Split('.', '-', '+');
        var numbers = new List<int>();

        foreach (var piece in pieces)
        {
            if (!int.TryParse(piece, out var value))
            {
                break;
            }

            numbers.Add(value);
        }

        return numbers.ToArray();
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

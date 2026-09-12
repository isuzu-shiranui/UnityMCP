using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Discovery;

/// <summary>How the Unity package got into a project, which decides whether it can be updated.</summary>
public enum PackageChannel
{
    /// <summary>Nothing in the project refers to the package.</summary>
    Absent,

    /// <summary>A git URL in the manifest. The reference names the version, so it can be rewritten.</summary>
    Git,

    /// <summary>A version from a registry. The same, with a plainer string.</summary>
    Registry,

    /// <summary>A path on this machine. Someone's working copy, not an installation.</summary>
    Local,

    /// <summary>A real folder under Packages/, which beats whatever the manifest says.</summary>
    Embedded,

    /// <summary>VCC or ALCOM put it there and keeps its own record of it.</summary>
    Vpm,
}

/// <summary>
/// Reads a Unity project to find out where its copy of the package came from.
/// </summary>
/// <remarks>
/// Four of the six answers are reasons not to touch anything, and each has a different next step
/// for the person reading. Getting the answer wrong is worse than not answering: rewriting the
/// manifest of a project whose real package sits in a folder under <c>Packages/</c> changes a line
/// nobody reads and leaves the version exactly where it was.
/// </remarks>
public static class PackageInstall
{
    public const string PackageId = "jp.shiranui-isuzu.unity-mcp";

    /// <summary>What the project says, and the one sentence that follows from it.</summary>
    public sealed record Install(PackageChannel Channel, string? Reference, string? Version)
    {
        /// <summary>Whether this CLI can move the project to another version on its own.</summary>
        public bool Updatable => Channel is PackageChannel.Git or PackageChannel.Registry;
    }

    /// <summary>Reads <paramref name="projectPath"/> and says where its package came from.</summary>
    public static Install Read(string projectPath)
    {
        var packages = Path.Combine(projectPath, "Packages");

        // Checked first because it wins: Unity takes a folder under Packages/ over any manifest
        // entry naming the same id, and says nothing about having done so.
        if (Directory.Exists(Path.Combine(packages, PackageId)))
        {
            return new Install(PackageChannel.Embedded, Path.Combine(packages, PackageId), EmbeddedVersion(packages));
        }

        if (Locked(Path.Combine(packages, "vpm-manifest.json")) is { } locked)
        {
            return new Install(PackageChannel.Vpm, "vpm-manifest.json", locked);
        }

        var dependency = Dependency(Path.Combine(packages, "manifest.json"));

        if (dependency is null)
        {
            return new Install(PackageChannel.Absent, null, null);
        }

        if (dependency.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return new Install(PackageChannel.Local, dependency, null);
        }

        if (dependency.Contains("://", StringComparison.Ordinal)
            || dependency.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return new Install(PackageChannel.Git, dependency, GitVersion(dependency));
        }

        return new Install(PackageChannel.Registry, dependency, dependency);
    }

    /// <summary>The dependency rewritten to ask for <paramref name="version"/>, or null when it cannot be.</summary>
    /// <remarks>
    /// A git dependency carries its version in the fragment after <c>#</c>, which is a tag rather
    /// than a semver, so the <c>v</c> goes back on. Anything already there is replaced rather than
    /// appended: two fragments is not a URL Unity resolves, it is one it reports as unreachable.
    /// </remarks>
    public static string? Retarget(Install install, string version)
    {
        if (version.Length == 0)
        {
            return null;
        }

        return install.Channel switch
        {
            PackageChannel.Registry => version,
            PackageChannel.Git => WithoutFragment(install.Reference!) + "#v" + version.TrimStart('v', 'V'),
            _ => null,
        };
    }

    /// <summary>Writes the dependency back, leaving the rest of the manifest as it was.</summary>
    /// <remarks>
    /// Parsed and re-serialised rather than substituted as text: a project's manifest carries
    /// scoped registries and comments people care about, and a regular expression that finds the
    /// right line in one project finds the wrong one in the next.
    /// </remarks>
    public static void WriteDependency(string projectPath, string reference)
    {
        var path = Path.Combine(projectPath, "Packages", "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                       ?? throw new IOException($"{path} is not a JSON object.");

        if (manifest["dependencies"] is not JsonObject dependencies)
        {
            throw new IOException($"{path} has no dependencies object.");
        }

        dependencies[PackageId] = reference;

        File.WriteAllText(
            path,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string WithoutFragment(string url)
    {
        var hash = url.IndexOf('#');

        return hash < 0 ? url : url[..hash];
    }

    private static string? GitVersion(string url)
    {
        var hash = url.IndexOf('#');

        return hash < 0 || hash == url.Length - 1 ? null : url[(hash + 1)..];
    }

    private static string? Dependency(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));

            return document.RootElement.TryGetProperty("dependencies", out var dependencies)
                   && dependencies.TryGetProperty(PackageId, out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The version VCC or ALCOM recorded, from either half of its manifest.</summary>
    private static string? Locked(string vpmManifestPath)
    {
        try
        {
            if (!File.Exists(vpmManifestPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(vpmManifestPath));

            foreach (var section in new[] { "locked", "dependencies" })
            {
                if (document.RootElement.TryGetProperty(section, out var entries)
                    && entries.TryGetProperty(PackageId, out var entry)
                    && entry.TryGetProperty("version", out var version))
                {
                    return version.GetString();
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? EmbeddedVersion(string packagesPath)
    {
        try
        {
            var manifest = Path.Combine(packagesPath, PackageId, "package.json");

            if (!File.Exists(manifest))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifest));

            return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

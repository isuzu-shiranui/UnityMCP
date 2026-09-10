using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace IsuzuUnityCli.Housekeeping;

/// <summary>
/// Installs the skill that ships inside this executable.
/// It is embedded rather than copied from a directory beside the binary because the binary is
/// often the only file the user has: the release asset is one self-contained executable.
/// </summary>
public static class SkillInstaller
{
    public const string SkillName = "isuzu-unity-cli";

    /// <summary>The v3 skill, installed by the npm package this tool replaces.</summary>
    public const string LegacySkillName = "isuzu-unity-mcp";

    private const string ResourcePrefix = "skills/isuzu-unity-cli/";

    private const string ResourceName = ResourcePrefix + "SKILL.md";

    public static string Content()
    {
        return TextOf(ResourceName);
    }

    /// <summary>
    /// Every file of the skill, by its path under the skill folder.
    /// </summary>
    /// <remarks>
    /// The guide points at reference pages beside it, so installing SKILL.md alone leaves those
    /// links pointing at files that are not there.
    /// </remarks>
    public static IEnumerable<(string Relative, string Text)> Files()
    {
        foreach (var name in typeof(SkillInstaller).Assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            yield return (name.Substring(ResourcePrefix.Length), TextOf(name));
        }
    }

    private static string TextOf(string resource)
    {
        using var stream = typeof(SkillInstaller).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"The skill is missing from this build. It is embedded as {resource}; " +
                "this executable was not built from a complete checkout.");

        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    /// <summary>Skills any agent on the machine reads, whoever installed them.</summary>
    /// <remarks>
    /// Not somewhere this tool writes. It is looked at because a guide left there is read
    /// alongside the installed one, and an agent following an obsolete guide fails in ways the
    /// current one cannot explain.
    /// </remarks>
    public static string SharedSkillsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");

    /// <summary>
    /// Guides directly under <paramref name="skillsDirectory"/> that describe the HTTP interface
    /// this server replaced.
    /// </summary>
    /// <remarks>
    /// Matched on content rather than on the folder name. Other Unity MCP servers install under
    /// names like "unity-mcp" too, and calling one of those an old copy of this guide would be
    /// wrong; the fixed port range together with the endpoint paths belongs to this project's
    /// HTTP interface, and the current guide carries neither.
    /// </remarks>
    public static IEnumerable<string> ObsoleteGuides(string skillsDirectory)
    {
        if (!Directory.Exists(skillsDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(skillsDirectory))
        {
            var file = Path.Combine(directory, "SKILL.md");
            string? text;

            try
            {
                text = File.Exists(file) ? File.ReadAllText(file) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (text is not null
                && text.Contains("27182", StringComparison.Ordinal)
                && text.Contains("/execute_code", StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }

    public static string DirectoryFor(string skillsDirectory) => Path.Combine(skillsDirectory, SkillName);

    public static string FileFor(string skillsDirectory) => Path.Combine(DirectoryFor(skillsDirectory), "SKILL.md");

    /// <summary>True when the installed copy differs from the one in this executable, or is missing.</summary>
    public static bool IsStale(string skillsDirectory)
    {
        var directory = DirectoryFor(skillsDirectory);

        try
        {
            foreach (var (relative, text) in Files())
            {
                var file = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(file)
                    || !Digest(File.ReadAllBytes(file)).SequenceEqual(Digest(Encoding.UTF8.GetBytes(text))))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return true;
        }

        return false;
    }

    public static bool IsInstalled(string skillsDirectory) => File.Exists(FileFor(skillsDirectory));

    /// <summary>
    /// Writes the skill and returns where it went.
    /// Staged in a sibling directory and swapped in at the end: writing straight into the
    /// destination means a failure part-way leaves the user with no skill at all, having
    /// destroyed the working one they had.
    /// </summary>
    public static string Install(string skillsDirectory)
    {
        var destination = DirectoryFor(skillsDirectory);
        var staging = destination + ".incoming";

        DeleteDirectory(staging);
        Directory.CreateDirectory(staging);

        try
        {
            foreach (var (relative, text) in Files())
            {
                var file = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(file) ?? staging);
                File.WriteAllText(file, text, new UTF8Encoding(false));
            }
        }
        catch
        {
            DeleteDirectory(staging);
            throw;
        }

        DeleteDirectory(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? skillsDirectory);
        Directory.Move(staging, destination);
        return destination;
    }

    /// <summary>Removes the v3 skill folder, and says whether there was one.</summary>
    public static bool RemoveLegacy(string skillsDirectory)
    {
        var legacy = Path.Combine(skillsDirectory, LegacySkillName);

        if (!Directory.Exists(legacy))
        {
            return false;
        }

        DeleteDirectory(legacy);
        return true;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static byte[] Digest(byte[] bytes) => SHA256.HashData(bytes);
}

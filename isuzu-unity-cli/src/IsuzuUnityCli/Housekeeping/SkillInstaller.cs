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

    private const string ResourceName = "skills/isuzu-unity-cli/SKILL.md";

    public static string Content()
    {
        using var stream = typeof(SkillInstaller).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The skill is missing from this build. It is embedded as {ResourceName}; " +
                "this executable was not built from a complete checkout.");

        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        return reader.ReadToEnd();
    }

    public static string DirectoryFor(string skillsDirectory) => Path.Combine(skillsDirectory, SkillName);

    public static string FileFor(string skillsDirectory) => Path.Combine(DirectoryFor(skillsDirectory), "SKILL.md");

    /// <summary>True when the installed copy differs from the one in this executable, or is missing.</summary>
    public static bool IsStale(string skillsDirectory)
    {
        var file = FileFor(skillsDirectory);

        if (!File.Exists(file))
        {
            return true;
        }

        try
        {
            return !Digest(File.ReadAllBytes(file)).SequenceEqual(Digest(Encoding.UTF8.GetBytes(Content())));
        }
        catch (IOException)
        {
            return true;
        }
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
            File.WriteAllText(Path.Combine(staging, "SKILL.md"), Content(), new UTF8Encoding(false));
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

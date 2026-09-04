using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Housekeeping;

public sealed class InventoryItem
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public bool Exists { get; init; }
    public string? Detail { get; init; }

    /// <summary>True when <c>uninstall</c> removes this path outright.</summary>
    public bool Removable { get; init; }
}

/// <summary>
/// Everything on disk that belongs to this tool, whether or not it currently exists.
/// <c>doctor</c> prints it and <c>uninstall</c> removes it, from the same list, so nothing can
/// be installed without a matching way to remove it.
/// </summary>
public static class StateInventory
{
    public static IReadOnlyList<InventoryItem> Build()
    {
        var items = new List<InventoryItem>();
        var primary = StatePaths.PrimaryRoot();

        foreach (var root in StatePaths.Roots())
        {
            items.Add(new InventoryItem
            {
                Path = root,
                Kind = root == primary ? "state root (written here)" : "state root (scanned)",
                Exists = Directory.Exists(root),
                Removable = true,
            });
        }

        AddCounted(items, StatePaths.DescriptorDirectories(), "Editor descriptors", "*.json", "descriptor");
        AddCounted(items, StatePaths.TokenDirectories(), "Editor tokens", "*.token", "token");
        AddCounted(items, StatePaths.CacheDirectories(), "tool catalog cache", "*.json", "file");

        foreach (var legacy in StatePaths.LegacyPaths())
        {
            items.Add(new InventoryItem
            {
                Path = legacy,
                Kind = "legacy (no longer written)",
                Exists = Directory.Exists(legacy) || File.Exists(legacy),
                Removable = true,
            });
        }

        return items;
    }

    /// <summary>How many files of a kind exist, summed over every state root that is scanned.</summary>
    public static int Count(IEnumerable<string> directories, string pattern)
    {
        var total = 0;

        foreach (var directory in directories)
        {
            total += Names(directory, pattern)?.Length ?? 0;
        }

        return total;
    }

    private static void AddCounted(
        List<InventoryItem> items,
        IEnumerable<string> directories,
        string kind,
        string pattern,
        string noun)
    {
        foreach (var directory in directories)
        {
            var names = Names(directory, pattern);

            items.Add(new InventoryItem
            {
                Path = directory,
                Kind = kind,
                Exists = Directory.Exists(directory),
                Detail = names is null ? null : $"{names.Length} {noun}(s)",
                Removable = true,
            });
        }
    }

    private static string[]? Names(string directory, string pattern)
    {
        try
        {
            return Directory.GetFiles(directory, pattern);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

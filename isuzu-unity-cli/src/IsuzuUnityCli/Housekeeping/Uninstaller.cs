using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;

namespace IsuzuUnityCli.Housekeeping;

/// <summary>One MCP entry that is present in a config file and would be taken out of it.</summary>
public sealed class ConfigEntryRemoval
{
    public required AgentTarget Target { get; init; }
    public required string ConfigPath { get; init; }

    /// <summary>Null for a TOML config, where the table name identifies the entry instead.</summary>
    public IReadOnlyList<string>? JsonPath { get; init; }

    public string Description
    {
        get
        {
            // Claude Code holds one entry per project, so the path alone would not say which.
            var scope = JsonPath is ["projects", var root, ..] ? $" for {root}" : "";
            return $"the {AgentCatalog.ServerName} entry{scope} in {ConfigPath}";
        }
    }
}

public sealed class UninstallPlan
{
    public List<ConfigEntryRemoval> ConfigEntries { get; } = new();
    public List<string> Skills { get; } = new();
    public List<string> State { get; } = new();

    public bool IsEmpty => ConfigEntries.Count == 0 && Skills.Count == 0 && State.Count == 0;
}

public static class Uninstaller
{
    /// <summary>
    /// Refuses while an Editor is running: its descriptor would be republished a moment later,
    /// and reporting a clean uninstall that immediately un-cleans itself would be a lie.
    /// </summary>
    public static void EnsureNothingRunning(IReadOnlyList<InstanceDescriptor> running)
    {
        if (running.Count == 0)
        {
            return;
        }

        throw new CliException(
            "These Editors are still running and would republish their descriptors: " +
            ProjectMatcher.Names(running) + ". Close them first.");
    }

    /// <summary>
    /// Everything that exists and would be removed.
    /// Project-scoped configs are looked for under the project roots the descriptors name, and
    /// nowhere else: scanning the disk for <c>.mcp.json</c> files would reach into repositories
    /// that have nothing to do with Unity.
    /// </summary>
    public static UninstallPlan Plan(
        IReadOnlyList<AgentTarget> agents,
        IReadOnlyList<InstanceDescriptor> knownDescriptors,
        bool includeSkills)
    {
        var plan = new UninstallPlan();
        var projectRoots = knownDescriptors
            .Select(descriptor => ProjectMatcher.ProjectRootOf(descriptor.ProjectPath))
            .Where(root => root.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var agent in agents)
        {
            // Claude Code keys its entry by project and VS Code keeps one file per project, so
            // both are looked at once per known project rather than once per agent.
            if (agent.IsProjectScoped || agent.Name == "claude-code")
            {
                // The entries are per project, but the file an agent keeps them in is not, and a
                // backup beside it is there whether or not any Editor is running.
                if (agent.ConfigPath is not null)
                {
                    AddStrayBackup(plan, agent.ConfigPath);
                }

                foreach (var root in projectRoots)
                {
                    AddIfPresent(plan, agent, agent.ConfigPathFor(root), root);
                }

                continue;
            }

            AddIfPresent(plan, agent, agent.ConfigPath!, "");
        }

        // Claude Code also reads a per-repository .mcp.json, which `setup --scope project` writes.
        var claudeCode = agents.FirstOrDefault(agent => agent.Name == "claude-code");

        if (claudeCode is not null)
        {
            foreach (var root in projectRoots)
            {
                var file = Path.Combine(root, ".mcp.json");

                // This loop does not go through AddIfPresent, so the sweep for a leftover backup
                // has to be repeated rather than inherited.
                AddStrayBackup(plan, file);

                if (ContainsJsonEntry(file, ["mcpServers", AgentCatalog.ServerName]))
                {
                    plan.ConfigEntries.Add(new ConfigEntryRemoval
                    {
                        Target = claudeCode,
                        ConfigPath = file,
                        JsonPath = ["mcpServers", AgentCatalog.ServerName],
                    });
                }
            }
        }

        if (includeSkills)
        {
            foreach (var directory in agents.Where(a => a.SkillsDirectory is not null).Select(a => a.SkillsDirectory!).Distinct(StringComparer.Ordinal))
            {
                foreach (var name in new[] { SkillInstaller.SkillName, SkillInstaller.LegacySkillName })
                {
                    var path = Path.Combine(directory, name);

                    if (Directory.Exists(path))
                    {
                        plan.Skills.Add(path);
                    }
                }
            }
        }

        // The roots are left out here and deleted afterwards, once they are empty, so a file
        // someone else put there is never taken with them.
        foreach (var item in StateInventory.Build())
        {
            if (item is { Exists: true, Removable: true } && !item.Kind.StartsWith("state root", StringComparison.Ordinal))
            {
                plan.State.Add(item.Path);
            }
        }

        return plan;
    }

    /// <summary>Applies a plan, returning one line per thing removed and one per failure.</summary>
    public static (List<string> Removed, List<string> Failed) Apply(UninstallPlan plan)
    {
        var removed = new List<string>();
        var failed = new List<string>();

        foreach (var entry in plan.ConfigEntries)
        {
            try
            {
                if (RemoveEntry(entry, out var backupLeft))
                {
                    removed.Add(entry.Description);
                }

                if (backupLeft != null)
                {
                    failed.Add($"{backupLeft}: the copy of the config as it was could not be removed, "
                        + "and it holds the token");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or TomlEditException)
            {
                failed.Add($"{entry.ConfigPath}: {e.Message}");
            }
        }

        // A config edit that failed leaves the config as it was, which is the one moment the
        // copy beside it matters. Removing it here would take the way back with it.
        var keep = failed.Count > 0
            ? plan.ConfigEntries
                .Select(entry => JsonConfigEditor.BackupFor(entry.ConfigPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in plan.Skills.Concat(plan.State).Where(path => !keep.Contains(path)))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else
                {
                    continue;
                }

                removed.Add(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{path}: {e.Message}");
            }
        }

        // The roots themselves go once their contents have, but only when nothing else lives there.
        foreach (var root in StatePaths.Roots())
        {
            TryRemoveEmptyDirectory(root, removed);
        }

        return (removed, failed);
    }

    private static bool RemoveEntry(ConfigEntryRemoval entry, out string? backupLeft)
    {
        backupLeft = null;

        if (entry.Target.Format == ConfigFormat.Toml)
        {
            var content = File.ReadAllText(entry.ConfigPath, Encoding.UTF8);
            var updated = TomlConfigEditor.Remove(content, McpServerEntry.TomlTableName());

            if (updated is null)
            {
                return false;
            }

            JsonConfigEditor.WriteText(entry.ConfigPath, updated);
            backupLeft = DiscardTheBackupOfWhatWasRemoved(entry.ConfigPath);
            return true;
        }

        var root = JsonConfigEditor.Read(entry.ConfigPath);

        if (!JsonConfigEditor.Remove(root, entry.JsonPath!))
        {
            return false;
        }

        if (entry.Target.Name == "vscode")
        {
            McpServerEntry.RemoveTokenInput(root);
        }

        JsonConfigEditor.Write(entry.ConfigPath, root);
        backupLeft = DiscardTheBackupOfWhatWasRemoved(entry.ConfigPath);
        return true;
    }

    /// <summary>
    /// Takes away the copy the write kept of the config as it was.
    /// </summary>
    /// <remarks>
    /// That copy is the config with the entry still in it, bearer token and all, so leaving it is
    /// leaving the credential this command was run to remove. A backup is worth having for an
    /// edit; it is not worth having for a deletion.
    /// </remarks>
    /// <summary>The backup path when it could not be removed, or null when it is gone.</summary>
    private static string? DiscardTheBackupOfWhatWasRemoved(string configPath)
    {
        var backup = JsonConfigEditor.BackupFor(configPath);

        try
        {
            File.Delete(backup);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Swallowing this would report a clean uninstall while the token is still on disk.
            return backup;
        }
    }

    /// <summary>
    /// Lists a backup an earlier write left beside a config.
    /// </summary>
    /// <remarks>
    /// That file is the config with the entry still in it, bearer token and all. Removing the
    /// entry discards the copy it just made, but a copy from a run that was interrupted, or from
    /// a run where the entry had already gone, is left for this to find. Putting it in the plan
    /// is what shows it to the person and what takes it away.
    /// </remarks>
    private static void AddStrayBackup(UninstallPlan plan, string configPath)
    {
        var backup = JsonConfigEditor.BackupFor(configPath);

        if (!File.Exists(backup) || plan.State.Contains(backup, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        // A config that cannot be used is the one case where the copy beside it is the only way
        // back, and it is also the case that reads as "no entry to remove" — so removing the copy
        // would take the config's other servers and settings with it and report success.
        if (File.Exists(configPath) && !HoldsSomethingToRemoveFrom(configPath))
        {
            return;
        }

        plan.State.Add(backup);
    }

    /// <summary>
    /// Whether the file is a config this command could act on, rather than one whose copy is the
    /// only thing left.
    /// </summary>
    /// <remarks>
    /// An empty file passes parsing — a blank config is a new config, which is right when nothing
    /// was there and wrong when something was. Beside a backup it means a write did not finish,
    /// so the copy is the way back and not litter.
    /// </remarks>
    private static bool HoldsSomethingToRemoveFrom(string configPath)
    {
        try
        {
            if (new FileInfo(configPath).Length == 0)
            {
                return false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return CanBeRead(configPath);
    }

    private static bool CanBeRead(string configPath)
    {
        try
        {
            if (Path.GetExtension(configPath).Equals(".toml", StringComparison.OrdinalIgnoreCase))
            {
                // Reading the bytes says nothing about whether they parse, and a config that does
                // not parse is exactly the one whose copy is the only way back.
                _ = TomlConfigEditor.Remove(File.ReadAllText(configPath, Encoding.UTF8), McpServerEntry.TomlTableName());
                return true;
            }

            _ = JsonConfigEditor.Read(configPath);
            return true;
        }
        catch (Exception e) when (e is JsonException or TomlEditException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AddIfPresent(UninstallPlan plan, AgentTarget agent, string configPath, string projectRoot)
    {
        AddStrayBackup(plan, configPath);

        if (agent.Format == ConfigFormat.Toml)
        {
            if (ContainsTomlEntry(configPath))
            {
                plan.ConfigEntries.Add(new ConfigEntryRemoval { Target = agent, ConfigPath = configPath });
            }

            return;
        }

        var paths = new List<IReadOnlyList<string>> { McpServerEntry.PathFor(agent, projectRoot) };

        // An earlier Windows build filed the entry under a backslash key. Leaving it there leaves
        // the token there too.
        var superseded = McpServerEntry.SupersededClaudeCodePath(agent, projectRoot);

        if (superseded != null)
        {
            paths.Add(superseded);
        }

        foreach (var path in paths)
        {
            if (ContainsJsonEntry(configPath, path))
            {
                plan.ConfigEntries.Add(new ConfigEntryRemoval { Target = agent, ConfigPath = configPath, JsonPath = path });
            }
        }
    }

    private static bool ContainsJsonEntry(string configPath, IReadOnlyList<string> path)
    {
        if (!File.Exists(configPath))
        {
            return false;
        }

        try
        {
            return JsonConfigEditor.Find(JsonConfigEditor.Read(configPath), path) is not null;
        }
        catch (Exception e) when (e is JsonException or TomlEditException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsTomlEntry(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return false;
        }

        try
        {
            return TomlConfigEditor.Read(File.ReadAllText(configPath, Encoding.UTF8), McpServerEntry.TomlTableName()) is not null;
        }
        catch (Exception e) when (e is TomlEditException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryRemoveEmptyDirectory(string path, List<string> removed)
    {
        try
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
            {
                Directory.Delete(path);
                removed.Add(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

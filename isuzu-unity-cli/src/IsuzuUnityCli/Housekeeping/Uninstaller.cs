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
                if (RemoveEntry(entry))
                {
                    removed.Add(entry.Description);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or TomlEditException)
            {
                failed.Add($"{entry.ConfigPath}: {e.Message}");
            }
        }

        foreach (var path in plan.Skills.Concat(plan.State))
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

    private static bool RemoveEntry(ConfigEntryRemoval entry)
    {
        if (entry.Target.Format == ConfigFormat.Toml)
        {
            var content = File.ReadAllText(entry.ConfigPath, Encoding.UTF8);
            var updated = TomlConfigEditor.Remove(content, McpServerEntry.TomlTableName());

            if (updated is null)
            {
                return false;
            }

            JsonConfigEditor.WriteText(entry.ConfigPath, updated);
            DiscardTheBackupOfWhatWasRemoved(entry.ConfigPath);
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
        DiscardTheBackupOfWhatWasRemoved(entry.ConfigPath);
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
    private static void DiscardTheBackupOfWhatWasRemoved(string configPath)
    {
        try
        {
            File.Delete(JsonConfigEditor.BackupFor(configPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Reported by the caller's own listing rather than thrown: the entry itself is gone.
        }
    }

    private static void AddIfPresent(UninstallPlan plan, AgentTarget agent, string configPath, string projectRoot)
    {
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
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
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

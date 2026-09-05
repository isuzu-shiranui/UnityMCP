using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Housekeeping;

namespace IsuzuUnityCli.Commands;

public static class SetupCommand
{
    /// <summary>Agents with no skill mechanism have nothing to install but the MCP entry.</summary>
    private static readonly string[] SkillCapableAgents = ["claude-code", "codex"];

    /// <summary>A skipped agent is neither a failure nor a reason to tell the user to restart it.</summary>
    private enum McpRegistration
    {
        Written,
        Skipped,
        Failed,
    }

    public static int Run(ParsedArgs parsed, CommandContext context)
    {
        var chosen = Chosen(parsed, context);

        if (chosen.Count == 0)
        {
            return 1;
        }

        var scope = parsed.Option("scope") ?? "user";

        if (scope is not ("user" or "project"))
        {
            context.Err.WriteLine($"Unknown scope '{scope}'. Use --scope user or --scope project.");
            return 1;
        }

        // CLI first: the skill is the default, the MCP entry is opt-in. Agents that cannot run a
        // skill get the entry only when named explicitly, so a bare `setup` that happens to
        // detect Cursor does not start registering servers everywhere.
        var explicitAgents = parsed.Option("agent") is not null || parsed.Option("client") is not null;
        var wantsMcp = parsed.HasFlag("mcp")
            || scope == "project"
            || (explicitAgents && chosen.Any(agent => !SkillCapableAgents.Contains(agent.Name, StringComparer.Ordinal)));

        InstanceDescriptor? descriptor = null;
        var failed = false;
        var registered = false;

        if (wantsMcp)
        {
            try
            {
                descriptor = context.ResolveInstance(parsed);
            }
            catch (CliException)
            {
                // Reported after the skills are installed, so a missing Editor does not cost
                // the user the half of setup that never needed one.
                descriptor = null;
            }
        }

        var projectRoot = descriptor is null ? "" : ProjectMatcher.ProjectRootOf(descriptor.ProjectPath);

        foreach (var agent in chosen)
        {
            var skillCapable = SkillCapableAgents.Contains(agent.Name, StringComparer.Ordinal);

            if (wantsMcp && descriptor is not null)
            {
                var outcome = Register(agent, descriptor, projectRoot, scope, context);

                registered |= outcome == McpRegistration.Written;
                failed |= outcome == McpRegistration.Failed;
            }
            else if (!wantsMcp && !skillCapable)
            {
                context.Out.WriteLine($"skipped {agent.Label}: no skill mechanism; pass --mcp to register the MCP endpoint");
            }

            if (parsed.HasFlag("no-skill") || agent.SkillsDirectory is null)
            {
                continue;
            }

            try
            {
                if (SkillInstaller.RemoveLegacy(agent.SkillsDirectory))
                {
                    context.Out.WriteLine($"removed v3 skill: {Path.Combine(agent.SkillsDirectory, SkillInstaller.LegacySkillName)}");
                }

                context.Out.WriteLine($"installed skill:  {SkillInstaller.Install(agent.SkillsDirectory)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                context.Err.WriteLine($"{agent.Label}: could not install the skill: {e.Message}");
                failed = true;
            }
        }

        if (registered)
        {
            context.Out.WriteLine();
            context.Out.WriteLine("Restart the agent so it picks up the new server.");
        }

        if (wantsMcp && descriptor is null)
        {
            context.Err.WriteLine(
                "setup --mcp needs a running Editor for the project: its URL and token come from the descriptor.");
            return 3;
        }

        return failed ? 1 : 0;
    }

    private static McpRegistration Register(
        AgentTarget agent,
        InstanceDescriptor descriptor,
        string projectRoot,
        string scope,
        CommandContext context)
    {
        if (scope == "project")
        {
            if (agent.Name != "claude-code")
            {
                context.Out.WriteLine($"skipped {agent.Label}: --scope project applies to claude-code only");
                return McpRegistration.Skipped;
            }

            return WriteProjectScope(agent, descriptor, projectRoot, context);
        }

        var configPath = agent.ConfigPathFor(projectRoot);

        if (agent.IsProjectScoped && projectRoot.Length == 0)
        {
            context.Out.WriteLine($"skipped {agent.Label}: its config lives in the Unity project, and no project was resolved");
            return McpRegistration.Skipped;
        }

        try
        {
            if (agent.Format == ConfigFormat.Toml)
            {
                var existing = File.Exists(configPath) ? File.ReadAllText(configPath, Encoding.UTF8) : "";
                var updated = TomlConfigEditor.Upsert(existing, McpServerEntry.TomlTableName(), McpServerEntry.TomlBody(descriptor));
                var directory = Path.GetDirectoryName(configPath);

                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(configPath, updated, new UTF8Encoding(false));
                JsonConfigEditor.RestrictToOwner(configPath);
            }
            else
            {
                var root = JsonConfigEditor.Read(configPath);
                JsonConfigEditor.Upsert(root, McpServerEntry.PathFor(agent, projectRoot), McpServerEntry.For(agent, descriptor, context.ExecutablePath));

                var superseded = McpServerEntry.SupersededClaudeCodePath(agent, projectRoot);

                if (superseded != null)
                {
                    JsonConfigEditor.Remove(root, superseded);
                }

                if (agent.Name == "vscode")
                {
                    McpServerEntry.EnsureTokenInput(root);
                }

                JsonConfigEditor.Write(configPath, root);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or TomlEditException)
        {
            // An unreadable config is left alone rather than replaced: it is far more likely to
            // hold settings worth keeping than to be disposable.
            context.Out.WriteLine($"skipped {agent.Label}: {e.Message}");
            return McpRegistration.Failed;
        }

        context.Out.WriteLine($"registered with {agent.Label}: {configPath}");
        return McpRegistration.Written;
    }

    /// <summary>
    /// Writes the repository's own <c>.mcp.json</c>, which is normally committed. The token goes
    /// in through the environment instead, so cloning the project does not hand it out.
    /// </summary>
    private static McpRegistration WriteProjectScope(AgentTarget agent, InstanceDescriptor descriptor, string projectRoot, CommandContext context)
    {
        if (projectRoot.Length == 0)
        {
            context.Out.WriteLine($"skipped {agent.Label}: no project root to write .mcp.json into");
            return McpRegistration.Failed;
        }

        var configPath = Path.Combine(projectRoot, ".mcp.json");

        try
        {
            var root = JsonConfigEditor.Read(configPath);
            JsonConfigEditor.Upsert(
                root,
                ["mcpServers", AgentCatalog.ServerName],
                McpServerEntry.Http(descriptor.McpUrlOrDefault, McpServerEntry.EnvironmentTokenReference, includeType: true));

            JsonConfigEditor.Write(configPath, root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Out.WriteLine($"skipped {agent.Label}: {e.Message}");
            return McpRegistration.Failed;
        }

        context.Out.WriteLine($"registered with {agent.Label}: {configPath}");
        context.Out.WriteLine(OperatingSystem.IsWindows()
            ? $"  $env:UNITY_MCP_TOKEN=\"{descriptor.Token}\""
            : $"  export UNITY_MCP_TOKEN={descriptor.Token}");

        return McpRegistration.Written;
    }

    /// <summary>Agents named with <c>--agent</c>, or every one found installed.</summary>
    private static List<AgentTarget> Chosen(ParsedArgs parsed, CommandContext context)
    {
        var all = AgentCatalog.All();
        var requested = parsed.Option("agent") ?? parsed.Option("client");

        if (requested is null)
        {
            var detected = all.Where(agent => agent.Detected).ToList();

            if (detected.Count == 0)
            {
                context.Err.WriteLine(
                    "No supported agent found on this machine. Pass --agent <name> to set one up anyway: " +
                    AgentCatalog.Names());
            }

            return detected;
        }

        var chosen = new List<AgentTarget>();

        foreach (var name in requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var found = all.FirstOrDefault(agent => string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase));

            if (found is null)
            {
                context.Err.WriteLine($"Unknown agent '{name}'. Known: {AgentCatalog.Names()}");
                return [];
            }

            chosen.Add(found);
        }

        return chosen;
    }
}

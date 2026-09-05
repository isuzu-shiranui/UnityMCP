using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Housekeeping;

namespace IsuzuUnityCli.Commands;

/// <summary>What an MCP entry in a config file says, and whether a running Editor agrees with it.</summary>
public sealed class EntryReport
{
    public required AgentTarget Agent { get; init; }
    public required string ConfigPath { get; init; }
    public IReadOnlyList<string>? JsonPath { get; init; }

    /// <summary>Which project the entry is for, when the file holds more than one.</summary>
    public string Scope { get; init; } = "";

    public required string Status { get; init; }

    /// <summary>Set when the entry is stale and this descriptor is what it should say.</summary>
    public InstanceDescriptor? Repair { get; init; }

    /// <summary>A key a repair takes away after writing <see cref="JsonPath"/>.</summary>
    public IReadOnlyList<string>? RetireJsonPath { get; init; }

    /// <summary>True for a config committed with the project, which never carries the token itself.</summary>
    public bool UsesPlaceholderToken { get; init; }
}

public static class DoctorCommand
{
    public static int Run(ParsedArgs parsed, CommandContext context)
    {
        var fix = parsed.HasFlag("fix");
        var agents = AgentCatalog.All();
        var running = context.ReadDescriptors();
        var known = context.ReadAllDescriptors();

        context.Out.WriteLine("Agents");

        foreach (var agent in agents)
        {
            var skills = agent.SkillsDirectory is null ? "no skills" : "skills supported";
            var path = agent.ConfigPath ?? $"<project>{Path.DirectorySeparatorChar}{agent.ProjectRelativeConfigPath}";

            context.Out.WriteLine(
                $"  {(agent.Detected ? "[found]  " : "[absent] ")}{agent.Name.PadRight(15)}" +
                $"{path.PadRight(60)} ({agent.Format.ToString().ToLowerInvariant()}, {skills})");
        }

        context.Out.WriteLine();
        context.Out.WriteLine("Skills");
        ReportSkills(agents, fix, context);

        context.Out.WriteLine();
        context.Out.WriteLine("MCP entries");
        ReportEntries(agents, running, known, fix, context);

        context.Out.WriteLine();
        context.Out.WriteLine("On disk");

        foreach (var item in StateInventory.Build())
        {
            var detail = item.Detail is null ? "" : $" ({item.Detail})";
            context.Out.WriteLine($"  {(item.Exists ? "[exists] " : "[absent] ")}{item.Kind.PadRight(28)} {item.Path}{detail}");
        }

        context.Out.WriteLine();
        context.Out.WriteLine("Running Editors");

        if (running.Count == 0)
        {
            context.Out.WriteLine("  none");
        }

        foreach (var descriptor in running)
        {
            context.Out.WriteLine($"  {descriptor.ProjectName} ({descriptor.UnityVersion}) {descriptor.McpUrlOrDefault} pid {descriptor.Pid}");

            if (descriptor.PortMismatch == true)
            {
                context.Out.WriteLine(
                    $"    warning: this Editor wanted port {descriptor.PreferredPort} and took {descriptor.Port}. " +
                    "Another instance holds the preferred port, so a config written for it points somewhere else.");
            }
        }

        // Always zero: doctor reports, and a report that fails the shell is a report nobody runs.
        return 0;
    }

    private static void ReportSkills(IReadOnlyList<AgentTarget> agents, bool fix, CommandContext context)
    {
        var directories = agents
            .Where(agent => agent.SkillsDirectory is not null)
            .Select(agent => agent.SkillsDirectory!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (directories.Count == 0)
        {
            context.Out.WriteLine("  no agent on this machine supports skills");
            return;
        }

        foreach (var directory in directories)
        {
            var destination = SkillInstaller.DirectoryFor(directory);

            if (!SkillInstaller.IsInstalled(directory))
            {
                context.Out.WriteLine($"  [absent]    {destination}");
                continue;
            }

            if (!SkillInstaller.IsStale(directory))
            {
                context.Out.WriteLine($"  [installed] {destination}");
                continue;
            }

            if (!fix)
            {
                context.Out.WriteLine($"  [stale]     {destination}");
                continue;
            }

            try
            {
                context.Out.WriteLine($"  [fixed]     {SkillInstaller.Install(directory)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                context.Out.WriteLine($"  [stale]     {destination} (could not reinstall: {e.Message})");
            }
        }
    }

    private static void ReportEntries(
        IReadOnlyList<AgentTarget> agents,
        IReadOnlyList<InstanceDescriptor> running,
        IReadOnlyList<InstanceDescriptor> known,
        bool fix,
        CommandContext context)
    {
        var reports = Collect(agents, running, known);

        if (reports.Count == 0)
        {
            context.Out.WriteLine("  none registered");
            return;
        }

        foreach (var report in reports)
        {
            var scope = report.Scope.Length == 0 ? "" : $" [{report.Scope}]";
            context.Out.WriteLine($"  {report.Agent.Name.PadRight(15)}{report.Status}{scope}");
            context.Out.WriteLine($"    {report.ConfigPath}");

            if (!fix || report.Repair is null)
            {
                continue;
            }

            try
            {
                Rewrite(report, context);
                context.Out.WriteLine($"    rewritten from the running Editor: {report.Repair.McpUrlOrDefault}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or TomlEditException)
            {
                context.Out.WriteLine($"    could not rewrite: {e.Message}");
            }
        }
    }

    public static List<EntryReport> Collect(
        IReadOnlyList<AgentTarget> agents,
        IReadOnlyList<InstanceDescriptor> running,
        IReadOnlyList<InstanceDescriptor> known)
    {
        var reports = new List<EntryReport>();
        var projectRoots = known
            .Select(descriptor => ProjectMatcher.ProjectRootOf(descriptor.ProjectPath))
            .Where(root => root.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var agent in agents)
        {
            switch (agent.Name)
            {
                case "claude-code":
                    CollectClaudeCode(reports, agent, running);
                    break;

                case "codex":
                    CollectCodex(reports, agent, running);
                    break;

                case "vscode":
                    foreach (var root in projectRoots)
                    {
                        CollectJson(reports, agent, agent.ConfigPathFor(root), McpServerEntry.PathFor(agent, root), root, running, placeholder: true);
                    }

                    break;

                default:
                    CollectJson(reports, agent, agent.ConfigPath!, McpServerEntry.PathFor(agent, ""), "", running, placeholder: false);
                    break;
            }
        }

        var claudeCode = agents.FirstOrDefault(agent => agent.Name == "claude-code");

        if (claudeCode is not null)
        {
            foreach (var root in projectRoots)
            {
                CollectJson(
                    reports,
                    claudeCode,
                    Path.Combine(root, ".mcp.json"),
                    ["mcpServers", AgentCatalog.ServerName],
                    root,
                    running,
                    placeholder: true);
            }
        }

        return reports;
    }

    private static void CollectClaudeCode(List<EntryReport> reports, AgentTarget agent, IReadOnlyList<InstanceDescriptor> running)
    {
        JsonObject root;

        try
        {
            root = JsonConfigEditor.Read(agent.ConfigPath!);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            reports.Add(new EntryReport { Agent = agent, ConfigPath = agent.ConfigPath!, Status = $"unreadable: {e.Message}" });
            return;
        }

        if (root["projects"] is not JsonObject projects)
        {
            return;
        }

        foreach (var pair in projects)
        {
            var entry = (pair.Value as JsonObject)?["mcpServers"] as JsonObject;

            if (entry?[AgentCatalog.ServerName] is not { } value)
            {
                continue;
            }

            // The key is the project root, so the Editor it belongs to is known exactly.
            // Compared through the same normalisation setup writes with, or a Windows key never
            // matches the Editor it belongs to and every entry reads as an orphan.
            var descriptor = running.FirstOrDefault(d =>
                string.Equals(
                    McpServerEntry.ClaudeCodeProjectKey(ProjectMatcher.ProjectRootOf(d.ProjectPath)),
                    McpServerEntry.ClaudeCodeProjectKey(pair.Key),
                    StringComparison.OrdinalIgnoreCase));

            var current = McpServerEntry.ClaudeCodeProjectKey(pair.Key);

            if (current != pair.Key)
            {
                // Claude Code reads this map by a forward-slash key, so an entry under any other
                // spelling is one it never sees, however well the URL and the token check out.
                // A repair writes the entry at the key it reads and takes the old one away.
                reports.Add(new EntryReport
                {
                    Agent = agent,
                    ConfigPath = agent.ConfigPath!,
                    JsonPath = ["projects", current, "mcpServers", AgentCatalog.ServerName],
                    RetireJsonPath = ["projects", pair.Key, "mcpServers", AgentCatalog.ServerName],
                    Scope = pair.Key,
                    Status = descriptor != null
                        ? "filed where Claude Code does not read it, by a build before 4.0.4"
                        : "filed where Claude Code does not read it, by a build before 4.0.4; "
                          + "start that project's Editor, or run setup --mcp",
                    Repair = descriptor,
                });

                continue;
            }

            reports.Add(Judge(agent, agent.ConfigPath!, ["projects", pair.Key, "mcpServers", AgentCatalog.ServerName], pair.Key, value, descriptor, running, placeholder: false));
        }
    }

    private static void CollectCodex(List<EntryReport> reports, AgentTarget agent, IReadOnlyList<InstanceDescriptor> running)
    {
        TomlServerEntry? entry;

        try
        {
            entry = File.Exists(agent.ConfigPath!)
                ? TomlConfigEditor.Read(File.ReadAllText(agent.ConfigPath!, Encoding.UTF8), McpServerEntry.TomlTableName())
                : null;
        }
        catch (Exception e) when (e is TomlEditException or IOException or UnauthorizedAccessException)
        {
            reports.Add(new EntryReport { Agent = agent, ConfigPath = agent.ConfigPath!, Status = $"unreadable: {e.Message}" });
            return;
        }

        if (entry is null)
        {
            return;
        }

        var descriptor = Locate(running, entry.Url, entry.Authorization);
        reports.Add(new EntryReport
        {
            Agent = agent,
            ConfigPath = agent.ConfigPath!,
            Status = Verdict(entry.Url, entry.Authorization, descriptor, running, placeholder: false, out var repair),
            Repair = repair ? descriptor : null,
        });
    }

    private static void CollectJson(
        List<EntryReport> reports,
        AgentTarget agent,
        string configPath,
        IReadOnlyList<string> jsonPath,
        string scope,
        IReadOnlyList<InstanceDescriptor> running,
        bool placeholder)
    {
        if (!File.Exists(configPath))
        {
            return;
        }

        JsonNode? entry;

        try
        {
            entry = JsonConfigEditor.Find(JsonConfigEditor.Read(configPath), jsonPath);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            reports.Add(new EntryReport { Agent = agent, ConfigPath = configPath, Scope = scope, Status = $"unreadable: {e.Message}" });
            return;
        }

        if (entry is null)
        {
            return;
        }

        var descriptor = agent.Transport == McpTransport.Stdio
            ? StdioTargetOf(entry, running)
            : Locate(running, McpServerEntry.Describe(entry).Url, McpServerEntry.Describe(entry).Authorization);

        reports.Add(Judge(agent, configPath, jsonPath, scope, entry, descriptor, running, placeholder));
    }

    private static EntryReport Judge(
        AgentTarget agent,
        string configPath,
        IReadOnlyList<string> jsonPath,
        string scope,
        JsonNode entry,
        InstanceDescriptor? descriptor,
        IReadOnlyList<InstanceDescriptor> running,
        bool placeholder)
    {
        if (agent.Transport == McpTransport.Stdio)
        {
            return new EntryReport
            {
                Agent = agent,
                ConfigPath = configPath,
                JsonPath = jsonPath,
                Scope = scope,
                Status = descriptor is null
                    ? "registered; its project is not running so it cannot be verified"
                    : "matches running Editor",
                UsesPlaceholderToken = placeholder,
            };
        }

        var (url, authorization) = McpServerEntry.Describe(entry);

        return new EntryReport
        {
            Agent = agent,
            ConfigPath = configPath,
            JsonPath = jsonPath,
            Scope = scope,
            Status = Verdict(url, authorization, descriptor, running, placeholder, out var repair),
            Repair = repair ? descriptor : null,
            UsesPlaceholderToken = placeholder,
        };
    }

    /// <summary>
    /// The token is fixed per project, so it identifies which Editor an entry was written for
    /// even after the port has moved. A config that carries a placeholder instead is matched on
    /// the URL alone.
    /// </summary>
    private static InstanceDescriptor? Locate(IReadOnlyList<InstanceDescriptor> running, string? url, string? authorization)
    {
        var byToken = authorization is null
            ? null
            : running.FirstOrDefault(d => McpServerEntry.BearerFor(d) == authorization);

        return byToken ?? (url is null ? null : running.FirstOrDefault(d => d.McpUrlOrDefault == url));
    }

    private static InstanceDescriptor? StdioTargetOf(JsonNode entry, IReadOnlyList<InstanceDescriptor> running)
    {
        if (entry is not JsonObject obj || obj["args"] is not JsonArray args)
        {
            return null;
        }

        var named = args
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var text) ? text : null)
            .ToList();

        var index = named.IndexOf("--project");

        return index < 0 || index + 1 >= named.Count
            ? null
            : running.FirstOrDefault(d => d.ProjectName == named[index + 1]);
    }

    private static string Verdict(
        string? url,
        string? authorization,
        InstanceDescriptor? descriptor,
        IReadOnlyList<InstanceDescriptor> running,
        bool placeholder,
        out bool repair)
    {
        repair = false;

        if (descriptor is null)
        {
            return running.Count == 0
                ? "registered; no Editor is running so it cannot be verified"
                : "registered; its project is not running so it cannot be verified";
        }

        if (url != descriptor.McpUrlOrDefault)
        {
            repair = true;
            return $"stale: points at {url ?? "nothing"}, the Editor is at {descriptor.McpUrlOrDefault}";
        }

        if (!placeholder && authorization != McpServerEntry.BearerFor(descriptor))
        {
            repair = true;
            return "stale: the token differs from the one the Editor published";
        }

        return "matches running Editor";
    }

    private static void Rewrite(EntryReport report, CommandContext context)
    {
        var descriptor = report.Repair!;

        if (report.Agent.Format == ConfigFormat.Toml)
        {
            var existing = File.Exists(report.ConfigPath) ? File.ReadAllText(report.ConfigPath, Encoding.UTF8) : "";
            var updated = TomlConfigEditor.Upsert(existing, McpServerEntry.TomlTableName(), McpServerEntry.TomlBody(descriptor));
            File.WriteAllText(report.ConfigPath, updated, new UTF8Encoding(false));
            return;
        }

        var root = JsonConfigEditor.Read(report.ConfigPath);

        // A committed config keeps its placeholder; only the URL was ever wrong.
        var entry = report.UsesPlaceholderToken && report.Agent.Name != "vscode"
            ? McpServerEntry.Http(descriptor.McpUrlOrDefault, McpServerEntry.EnvironmentTokenReference, includeType: true)
            : McpServerEntry.For(report.Agent, descriptor, context.ExecutablePath);

        JsonConfigEditor.Upsert(root, report.JsonPath!, entry);

        if (report.RetireJsonPath != null)
        {
            JsonConfigEditor.Remove(root, report.RetireJsonPath);
        }

        if (report.Agent.Name == "vscode")
        {
            McpServerEntry.EnsureTokenInput(root);
        }

        JsonConfigEditor.Write(report.ConfigPath, root);
    }
}

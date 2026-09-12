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

        var cli = CliInstall.Read(context.ExecutablePath);

        ReportExecutable(cli, context);

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
        ReportLeftFromV3(agents, running, context);

        context.Out.WriteLine();
        context.Out.WriteLine("On disk");

        foreach (var item in StateInventory.Build())
        {
            var detail = item.Detail is null ? "" : $" ({item.Detail})";
            context.Out.WriteLine($"  {(item.Exists ? "[exists] " : "[absent] ")}{item.Kind.PadRight(28)} {item.Path}{detail}");
        }

        context.Out.WriteLine();
        ReportRelease(cli, context);

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

            var skew = Skew(descriptor.ProtocolVersion, Program.Version());

            if (skew != null)
            {
                context.Out.WriteLine("    " + skew);

                var embedded = EmbeddedCopy(descriptor.ProjectPath);

                if (embedded != null)
                {
                    context.Out.WriteLine("    " + embedded);
                }
            }

            var samples = LeftBehindSamples(descriptor.ProjectPath);

            if (samples != null)
            {
                context.Out.WriteLine("    " + samples);
            }
        }

        // Always zero: doctor reports, and a report that fails the shell is a report nobody runs.
        return 0;
    }

    /// <summary>
    /// Where this build is, and what a caller typing the command's name actually gets.
    /// </summary>
    /// <remarks>
    /// A copy installed once and left behind stays first on PATH, and an agent driving the Editor
    /// through the command name runs that one: every fix made since goes unseen while the repo's
    /// own build passes its tests. Found by an agent hitting a bug that had already been fixed
    /// three versions earlier.
    /// </remarks>
    private static void ReportExecutable(CliInstall.Install cli, CommandContext context)
    {
        context.Out.WriteLine("Executable");
        context.Out.WriteLine($"  [running]  {context.ExecutablePath} ({Program.Version()})");
        context.Out.WriteLine($"  [channel]  installed {cli.Description}; update with: {cli.UpdateCommand}");

        var onPath = FirstOnPath();

        if (onPath is null)
        {
            context.Out.WriteLine("  [absent]   no isuzu-unity-cli on PATH; the command name will not resolve");
        }
        else if (!SamePath(onPath, context.ExecutablePath))
        {
            var version = VersionOf(onPath);

            var note = version is null ? "" : $" ({version})";
            var stale = version is not null && ReleaseCheck.IsNewer(Program.Version(), version);

            context.Out.WriteLine($"  {(stale ? "[stale]   " : "[other]   ")} {onPath}{note}");
            context.Out.WriteLine(
                stale
                    ? "    this is what the command name runs, and it is older than the build you are in. "
                      + "Replace it, or the fixes in this build are not the ones being used"
                    : "    this is what the command name runs, and it is not the build you are in");
        }

        context.Out.WriteLine();
    }

    /// <summary>
    /// Whether two paths name the same file.
    /// </summary>
    /// <remarks>
    /// Environment.ProcessPath is the resolved target on Linux, while a PATH entry usually is not:
    /// install.sh puts the binary under ~/.local/bin, which is commonly a link. Compared as text,
    /// every such installation reported that the command name runs some other build. Case is only
    /// ignored where the filesystem ignores it.
    /// </remarks>
    private static bool SamePath(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(left, right, comparison))
        {
            return true;
        }

        try
        {
            return string.Equals(
                new FileInfo(left).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? left,
                new FileInfo(right).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? right,
                comparison);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The first isuzu-unity-cli a shell would find, or null when there is none.</summary>
    private static string? FirstOnPath()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "isuzu-unity-cli.exe", "isuzu-unity-cli.cmd", "isuzu-unity-cli" }
            : new[] { "isuzu-unity-cli" };

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), name);

                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // A PATH entry that is not a usable directory is the shell's problem, not this.
                }
            }
        }

        return null;
    }

    /// <summary>The version another copy reports, or null when it cannot be read.</summary>
    private static string? VersionOf(string executable)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(executable);

            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Says so when a newer release exists. Never installs one.</summary>
    /// <remarks>
    /// Nothing told anyone a release had happened, so an installation stayed where it was until
    /// something broke. This says it and stops there: installing without being asked is what
    /// turns a bad release into a broken machine, and 'upgrade --release' is the way back when
    /// one turns out to be.
    /// </remarks>
    private static void ReportRelease(CliInstall.Install cli, CommandContext context)
    {
        string? tag;

        try
        {
            tag = ReleaseCheck.LatestTag(ReleaseCheck.FromGitHub, context.Cancellation).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return;
        }

        if (tag is null || !ReleaseCheck.IsNewer(tag, Program.Version()))
        {
            return;
        }

        context.Out.WriteLine("Release");

        if (cli.ReplacesItself)
        {
            context.Out.WriteLine(
                $"  {tag} is out and this is {Program.Version()}. "
                + "Install it with 'isuzu-unity-cli upgrade', and update the Unity package to match. "
                + "'upgrade --release <tag>' goes back if one turns out to be broken.");
        }
        else
        {
            context.Out.WriteLine($"  {tag} is out and this is {Program.Version()}. Update this CLI with: {cli.UpdateCommand}");

            if (cli.Channel is CliChannel.Winget)
            {
                context.Out.WriteLine("  " + CliInstall.WingetDelay);
            }

            context.Out.WriteLine("  Then 'isuzu-unity-cli update' updates the Unity package to match.");
        }

        context.Out.WriteLine();
    }

    /// <summary>
    /// Where a copy of the package sits inside the project, which is the copy the Editor loads.
    /// </summary>
    /// <remarks>
    /// A folder under Packages/ wins over the manifest entry of the same name, and Unity says
    /// nothing about it. Told to update through the Package Manager, someone with an embedded
    /// copy changes the manifest, sees the version stay where it was, and has no way to tell why.
    /// </remarks>
    public static string? EmbeddedCopy(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return null;
        }

        // The descriptor names the Assets folder; the packages sit beside it.
        var root = Path.GetDirectoryName(projectPath.TrimEnd('/', '\\'));

        if (root == null)
        {
            return null;
        }

        var embedded = Path.Combine(root, "Packages", "jp.shiranui-isuzu.unity-mcp");
        var manifest = Path.Combine(embedded, "package.json");

        if (!File.Exists(manifest))
        {
            return null;
        }

        return $"an embedded copy at {embedded} is what this Editor loads. A folder under "
               + "Packages/ wins over the manifest entry of the same name, so updating the "
               + "manifest changes nothing until that folder is removed or replaced.";
    }

    /// <summary>
    /// Samples an older version of the package imported, which it no longer ships.
    /// </summary>
    /// <remarks>
    /// Importing a sample copies it into Assets/, where it stays through every later upgrade.
    /// The 1.1.1 samples were written against an IMcpCommandHandler that no longer exists, so a
    /// project carrying them stops compiling and Unity opens asking whether to enter Safe Mode —
    /// with nothing on screen connecting that to a package update.
    /// </remarks>
    public static string? LeftBehindSamples(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
        {
            return null;
        }

        var samples = Path.Combine(projectPath, "Samples", "Unity MCP");

        if (!Directory.Exists(samples))
        {
            return null;
        }

        var versions = Directory.GetDirectories(samples).Select(Path.GetFileName).ToArray();
        var named = versions.Length == 0 ? "" : $" ({string.Join(", ", versions)})";

        return $"samples from an older release are still at {samples}{named}. This package ships "
               + "no samples now, and the ones it used to are written against APIs that are gone: "
               + "left in place they stop the project compiling. Delete that folder.";
    }

    /// <summary>Says which side is behind when the package and this CLI are not the same release.</summary>
    /// <remarks>
    /// The descriptor has carried protocolVersion all along and nothing compared it, so a CLI and
    /// a package from different releases failed in whatever way the missing piece happened to
    /// fail — a tool that is not there, an argument that is not read — with nothing pointing at
    /// the version.
    /// <para>
    /// Any difference is reported, not only a difference in the major. The two are released
    /// together under one number and the release refuses to publish them apart, so 4.0.0 against
    /// 4.3.0 is three releases of tools and fixes the older half does not have — which is the
    /// case that was silent while only the major was compared.
    /// </para>
    /// </remarks>
    public static string? Skew(string protocolVersion, string cliVersion)
    {
        if (string.IsNullOrEmpty(protocolVersion) || string.IsNullOrEmpty(cliVersion))
        {
            return null;
        }

        if (string.Equals(protocolVersion, cliVersion, StringComparison.Ordinal))
        {
            return null;
        }

        // Field by field rather than as text, so 4.10.0 is not read as older than 4.9.0.
        var editorIsBehind = ReleaseCheck.IsNewer(cliVersion, protocolVersion);
        var cliIsBehind = ReleaseCheck.IsNewer(protocolVersion, cliVersion);

        if (!editorIsBehind && !cliIsBehind)
        {
            // Two spellings of one version, or two versions neither of which parses.
            return null;
        }

        return editorIsBehind
            ? $"version skew: this Editor's package is {protocolVersion} and this CLI is "
              + $"{cliVersion}. 'isuzu-unity-cli update' lines the package up with the CLI."
            : $"version skew: this Editor's package is {protocolVersion} and this CLI is "
              + $"{cliVersion}. Update the CLI with 'isuzu-unity-cli update'.";
    }

    private static bool TryMajor(string version, out int major)
    {
        major = 0;

        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        var dot = version.IndexOf('.');
        var head = dot < 0 ? version : version.Substring(0, dot);

        return int.TryParse(head, out major);
    }

    /// <summary>
    /// What a v3 install leaves behind: the old skill folder, and the old MCP entry.
    /// </summary>
    /// <remarks>
    /// The entry is the one that costs something. It names a node script that
    /// <c>npm uninstall -g</c> removes, so the client tries to spawn a missing file every time it
    /// starts. Naming it here is the difference between a migration guide someone read once and
    /// an answer from the command that exists to say what is wrong.
    /// </remarks>
    private static void ReportLeftFromV3(
        IReadOnlyList<AgentTarget> agents,
        IReadOnlyList<InstanceDescriptor> running,
        CommandContext context)
    {
        var projectRoots = running
            .Select(descriptor => ProjectMatcher.ProjectRootOf(descriptor.ProjectPath))
            .Where(root => root.Length > 0)
            .DistinctBy(root => ProjectKey.Of(root) ?? root, StringComparer.Ordinal)
            .ToList();

        foreach (var agent in agents)
        {
            if (agent.SkillsDirectory is not null)
            {
                var legacySkill = Path.Combine(agent.SkillsDirectory, SkillInstaller.LegacySkillName);

                if (Directory.Exists(legacySkill))
                {
                    context.Out.WriteLine($"  [v3 leftover] {legacySkill}");
                    context.Out.WriteLine("    the v3 skill. setup removes it; so does uninstall");
                }
            }

            // A project-scoped agent has no single config, so its backups are looked for beside
            // the config in each project an Editor has published, and Claude Code's own
            // per-repository .mcp.json is looked at the same way.
            var configs = new List<string>();

            if (agent.ConfigPath is not null)
            {
                configs.Add(agent.ConfigPath);
            }

            if (agent.IsProjectScoped || agent.Name == "claude-code")
            {
                configs.AddRange(projectRoots.Select(agent.ConfigPathFor));
            }

            foreach (var backup in configs
                .Select(JsonConfigEditor.BackupFor)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists))
            {
                context.Out.WriteLine($"  [left behind] {backup}");
                context.Out.WriteLine("    a copy of the config as it was before an edit, so it holds "
                    + "whatever the config held, token included. uninstall takes it away");
            }

            if (agent.ConfigPath is null || agent.Format != ConfigFormat.Json || !File.Exists(agent.ConfigPath))
            {
                continue;
            }

            JsonObject root;

            try
            {
                root = JsonConfigEditor.Read(agent.ConfigPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            foreach (var path in LegacyEntryPaths(agent, root))
            {
                context.Out.WriteLine($"  [v3 leftover] {agent.Name}: {string.Join(" > ", path)}");
                context.Out.WriteLine($"    {agent.ConfigPath}");
                context.Out.WriteLine("    a v3 entry naming a node script npm has already removed, so the "
                    + "client fails to start it every launch. Remove it by hand");
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> LegacyEntryPaths(AgentTarget agent, JsonObject root)
    {
        const string legacy = SkillInstaller.LegacySkillName;

        if (root["mcpServers"] is JsonObject servers && servers.ContainsKey(legacy))
        {
            yield return ["mcpServers", legacy];
        }

        if (root["projects"] is not JsonObject projects)
        {
            yield break;
        }

        foreach (var pair in projects)
        {
            if ((pair.Value as JsonObject)?["mcpServers"] is JsonObject inner && inner.ContainsKey(legacy))
            {
                yield return ["projects", pair.Key, "mcpServers", legacy];
            }
        }
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

        // Reported and not removed, even with --fix: this tool did not put it there, and what
        // else a skills directory holds is the user's to decide.
        foreach (var guide in directories
            .Append(SkillInstaller.SharedSkillsDirectory)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(SkillInstaller.ObsoleteGuides))
        {
            context.Out.WriteLine($"  [old guide] {guide}");
            context.Out.WriteLine(
                "    describes the HTTP interface this server replaced. Agents read it alongside "
                + "the current guide; delete it");
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
            .DistinctBy(root => ProjectKey.Of(root) ?? root, StringComparer.Ordinal)
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

            // The key is the project root, so the Editor it belongs to is known exactly. Compared
            // as a reconnect compares, with the case kept: a folder that differs only in case can
            // be another project, and its token written here would hand the entry over.
            var key = ProjectKey.Of(pair.Key);
            var owners = key is null ? [] : running.Where(d => ProjectKey.Of(d.ProjectPath) == key).ToList();

            if (owners.Count > 1)
            {
                reports.Add(new EntryReport
                {
                    Agent = agent,
                    ConfigPath = agent.ConfigPath!,
                    Scope = pair.Key,
                    Status = $"cannot be verified: {owners.Count} running Editors publish this project",
                });

                continue;
            }

            var descriptor = owners.Count == 1 ? owners[0] : null;

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

            reports.Add(Judge(agent, agent.ConfigPath!, ["projects", pair.Key, "mcpServers", AgentCatalog.ServerName], pair.Key, value, descriptor, identified: true, running, placeholder: false));
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

        var descriptor = Locate(running, entry.Url, entry.Authorization, out var byToken);
        reports.Add(new EntryReport
        {
            Agent = agent,
            ConfigPath = agent.ConfigPath!,
            Status = Verdict(agent, entry.Url, entry.Authorization, descriptor, byToken, running, placeholder: false, out var repair),
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

        var byToken = false;
        var descriptor = agent.Transport == McpTransport.Stdio
            ? StdioTargetOf(entry, running)
            : Locate(running, McpServerEntry.Describe(entry).Url, McpServerEntry.Describe(entry).Authorization, out byToken);

        reports.Add(Judge(agent, configPath, jsonPath, scope, entry, descriptor, byToken, running, placeholder));
    }

    private static EntryReport Judge(
        AgentTarget agent,
        string configPath,
        IReadOnlyList<string> jsonPath,
        string scope,
        JsonNode entry,
        InstanceDescriptor? descriptor,
        bool identified,
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
            Status = Verdict(agent, url, authorization, descriptor, identified, running, placeholder, out var repair),
            Repair = repair ? descriptor : null,
            UsesPlaceholderToken = placeholder,
        };
    }

    /// <summary>
    /// The token is fixed per project, so it identifies which Editor an entry was written for
    /// even after the port has moved. A config that carries a placeholder instead is matched on
    /// the URL alone.
    /// </summary>
    /// <param name="byToken">Whether the token matched, which is what shows the entry is for that Editor's project.</param>
    private static InstanceDescriptor? Locate(
        IReadOnlyList<InstanceDescriptor> running, string? url, string? authorization, out bool byToken)
    {
        var tokenMatch = authorization is null
            ? null
            : running.FirstOrDefault(d => McpServerEntry.BearerFor(d) == authorization);

        byToken = tokenMatch is not null;
        return tokenMatch ?? (url is null ? null : running.FirstOrDefault(d => d.McpUrlOrDefault == url));
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

    /// <param name="identified">
    /// Whether the entry is known to be for <paramref name="descriptor"/>'s project, by its token or by
    /// the project key it is filed under, rather than only by naming the same URL.
    /// </param>
    private static string Verdict(
        AgentTarget agent,
        string? url,
        string? authorization,
        InstanceDescriptor? descriptor,
        bool identified,
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
            // A port is not a project. Once another project's Editor holds the port this entry
            // names, writing that Editor's token here would hand the entry to the other project.
            if (!identified)
            {
                return "stale: the token differs from the one the Editor at this URL published, and nothing shows "
                    + $"the entry is for that Editor's project. Run 'isuzu-unity-cli setup --mcp --agent {agent.Name}' "
                    + "in the project the entry is for";
            }

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
            JsonConfigEditor.WriteText(report.ConfigPath, updated);
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

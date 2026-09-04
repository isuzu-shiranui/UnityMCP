using System.Text.Json;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Serialization;

namespace IsuzuUnityCli.Commands;

public sealed class ProjectRow
{
    public string ProjectName { get; init; } = "";
    public string ProjectRoot { get; init; } = "";
    public string UnityVersion { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string McpUrl { get; init; } = "";
    public int Pid { get; init; }
    public string ProtocolVersion { get; init; } = "";
    public bool ContainsWorkingDirectory { get; init; }
}

public static class ProjectsCommand
{
    public static int Run(ParsedArgs parsed, CommandContext context)
    {
        var descriptors = context.ReadDescriptors();

        if (descriptors.Count == 0)
        {
            context.Err.WriteLine(InstanceResolver.NoneRunning);
            return 3;
        }

        var here = ProjectMatcher.ByWorkingDirectory(descriptors, context.WorkingDirectory);

        var rows = descriptors.Select(d => new ProjectRow
        {
            ProjectName = d.ProjectName,
            ProjectRoot = ProjectMatcher.ProjectRootOf(d.ProjectPath),
            UnityVersion = d.UnityVersion,
            Endpoint = d.Endpoint,
            McpUrl = d.McpUrlOrDefault,
            Pid = d.Pid,
            ProtocolVersion = d.ProtocolVersion,
            ContainsWorkingDirectory = here is not null && ReferenceEquals(here, d),
        }).ToList();

        JsonOutput.Print(context.Out, JsonSerializer.SerializeToNode(rows, CliJsonContext.Default.ListProjectRow));
        return 0;
    }
}

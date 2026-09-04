using IsuzuUnityCli.Agents;
using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Housekeeping;

namespace IsuzuUnityCli.Commands;

public static class UninstallCommand
{
    public static int Run(ParsedArgs parsed, CommandContext context)
    {
        // Checked before anything is touched, so a refusal leaves the machine exactly as it was.
        Uninstaller.EnsureNothingRunning(context.ReadDescriptors());

        var includeSkills = !parsed.HasFlag("no-skill");
        var agents = AgentCatalog.All().Where(agent => agent.Detected).ToList();
        var plan = Uninstaller.Plan(agents, context.ReadAllDescriptors(), includeSkills);

        if (!parsed.HasFlag("yes"))
        {
            if (plan.IsEmpty)
            {
                context.Out.WriteLine("Nothing to remove.");
                return 0;
            }

            // Listed before removing, by default: the alternative is a command that deletes
            // things the user has not seen named.
            context.Out.WriteLine("Would remove:");
            context.Out.WriteLine();

            foreach (var entry in plan.ConfigEntries)
            {
                context.Out.WriteLine($"  {entry.Description}");
            }

            foreach (var path in plan.Skills.Concat(plan.State))
            {
                context.Out.WriteLine($"  {path}");
            }

            context.Out.WriteLine();
            context.Out.WriteLine("Re-run with --yes to remove them.");
            return 0;
        }

        var (removed, failed) = Uninstaller.Apply(plan);

        foreach (var line in removed)
        {
            context.Out.WriteLine($"removed {line}");
        }

        foreach (var line in failed)
        {
            context.Err.WriteLine($"could not remove {line}");
        }

        context.Out.WriteLine();
        context.Out.WriteLine("The Unity package itself is removed through the Package Manager.");
        return failed.Count > 0 ? 1 : 0;
    }
}

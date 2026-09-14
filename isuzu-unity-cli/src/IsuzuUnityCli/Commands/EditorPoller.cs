using System.Diagnostics;

using IsuzuUnityCli.Cli;
using IsuzuUnityCli.Discovery;
using IsuzuUnityCli.Http;

namespace IsuzuUnityCli.Commands;

/// <summary>
/// Repeats a read against one Editor until its answer settles, across domain reloads.
/// </summary>
/// <remarks>
/// Only reads go through here. The listener is gone while a reload runs, and comes back on
/// another port with another token, so the descriptor is re-read and the same project is asked
/// again; a request that changes something must never be repeated this way.
/// </remarks>
public static class EditorPoller
{
    public sealed record Outcome(Envelope? Last, InstanceDescriptor Instance, bool TimedOut, string? Unreachable);

    /// <exception cref="CliException">Exit code 3 when the Editor keeps rejecting the published token.</exception>
    public static async Task<Outcome> Poll(
        CommandContext context,
        InstanceDescriptor instance,
        Func<InstanceDescriptor, CancellationToken, Task<Envelope>> read,
        Func<Envelope, bool> settled,
        Action<Envelope>? eachUnsettled,
        double timeoutSeconds,
        int pollIntervalMs,
        int unauthorizedGraceMs,
        bool readFirst = true)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        Envelope? last = null;
        string? unreachable = null;
        Stopwatch? rejectedSince = null;
        var reauthenticated = false;

        try
        {
            if (!readFirst)
            {
                await Task.Delay(pollIntervalMs, deadline.Token);
            }

            while (true)
            {
                Envelope envelope;

                try
                {
                    envelope = await read(instance, deadline.Token);
                }
                catch (UnityError e) when (e.HttpStatus is null || e.Code is "non_json" or "server_stopped" || e.HttpStatus == 401)
                {
                    var changed = false;

                    try
                    {
                        var refreshed = context.RefreshInstance(instance, cancellation: deadline.Token);
                        changed = refreshed.Endpoint != instance.Endpoint || refreshed.Token != instance.Token;
                        instance = refreshed;
                        unreachable = e.HttpStatus == 401
                            ? $"The Editor at {instance.Endpoint} rejects the token published for "
                              + $"{ProjectKey.Display(instance.ProjectPath)}. {InstanceResolver.SwitchByCommand}"
                            : e.Message;
                    }
                    catch (CliException refused)
                    {
                        // Briefly absent while the Editor rewrites its descriptor, or gone for good.
                        // Either way no other project is asked, and the reason is kept for the report.
                        unreachable = refused.Message;
                    }

                    if (e.HttpStatus == 401)
                    {
                        if (changed && !reauthenticated)
                        {
                            reauthenticated = true;
                        }
                        else
                        {
                            // A restarted Editor's descriptor can lag its restart, so a rejection is
                            // final only once the token has stayed the same for a while.
                            rejectedSince ??= Stopwatch.StartNew();

                            if (rejectedSince.ElapsedMilliseconds >= unauthorizedGraceMs)
                            {
                                throw new CliException(unreachable ?? e.Message, 3);
                            }
                        }
                    }

                    await Task.Delay(pollIntervalMs, deadline.Token);
                    continue;
                }

                unreachable = null;
                rejectedSince = null;
                reauthenticated = false;
                last = envelope;

                if (settled(envelope))
                {
                    return new Outcome(envelope, instance, false, null);
                }

                eachUnsettled?.Invoke(envelope);
                await Task.Delay(pollIntervalMs, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
        {
            return new Outcome(last, instance, true, unreachable);
        }
    }
}

using System.Text.Json.Nodes;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class VerifyTestOutcomeTests
{
    // test_results excludes passing tests before taking its limit, but skipped tests still
    // occupy that limit. Moving a failure past the last retained row must not change the verdict.
    [Theory]
    [InlineData(199, "failed", false)]
    [InlineData(200, "failed", false)]
    [InlineData(201, "failed", false)]
    [InlineData(199, "inconclusive", false)]
    [InlineData(200, "inconclusive", false)]
    [InlineData(201, "inconclusive", false)]
    [InlineData(199, "failed", true)]
    [InlineData(200, "failed", true)]
    [InlineData(201, "failed", true)]
    [InlineData(199, "inconclusive", true)]
    [InlineData(200, "inconclusive", true)]
    [InlineData(201, "inconclusive", true)]
    public async Task NonPassingOutcomeDoesNotDependOnItsPositionInTheDetails(
        int skipped, string outcome, bool raw)
    {
        var statuses = Enumerable.Repeat("skipped", skipped).Append(outcome).ToArray();
        using var server = ServerFor(CompletedRun(statuses));
        var result = await Verify(server, raw);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["/tools/test_run", "/tools/test_results", "/tools/console_read_logs"],
            server.Requests.Select(request => request.Path));
        var request = JsonNode.Parse(server.Requests[1].Body)!;
        Assert.False(request["include_passed"]!.GetValue<bool>());
        Assert.Equal(200, request["limit"]!.GetValue<int>());

        if (raw)
        {
            var summary = JsonNode.Parse(result.Output)!;
            Assert.False(summary["ok"]!.GetValue<bool>());
            Assert.Equal(outcome == "failed" ? 1 : 0, summary["tests"]!["failed"]!.GetValue<int>());
            Assert.Equal(outcome == "inconclusive" ? 1 : 0, summary["tests"]!["inconclusive"]!.GetValue<int>());
            Assert.Equal(skipped, summary["tests"]!["skipped"]!.GetValue<int>());
            Assert.Equal(skipped >= 200, summary["tests"]!["truncated"]!.GetValue<bool>());
            Assert.Equal(skipped < 200 ? 1 : 0, summary["tests"]!["failures"]!.AsArray().Count);
        }
        else
        {
            Assert.Contains(outcome == "failed" ? "1 failed" : "0 failed", result.Output);
            if (outcome == "inconclusive")
                Assert.Contains("1 inconclusive", result.Output);
            if (skipped >= 200)
                Assert.Contains("test details truncated", result.Output + result.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatingOnlySkippedTestsDoesNotFailTheRun(bool raw)
    {
        using var server = ServerFor(CompletedRun(Enumerable.Repeat("skipped", 201).ToArray()));
        var result = await Verify(server, raw);

        Assert.Equal(0, result.ExitCode);
        if (raw)
        {
            var summary = JsonNode.Parse(result.Output)!;
            Assert.True(summary["ok"]!.GetValue<bool>());
            Assert.Equal(0, summary["tests"]!["failed"]!.GetValue<int>());
            Assert.Equal(0, summary["tests"]!["inconclusive"]!.GetValue<int>());
            Assert.Equal(201, summary["tests"]!["skipped"]!.GetValue<int>());
            Assert.True(summary["tests"]!["truncated"]!.GetValue<bool>());
        }
        else
        {
            Assert.Contains("0 failed", result.Output);
            Assert.Contains("test details truncated", result.Output + result.Error);
        }
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    [InlineData("inconclusive", false)]
    [InlineData("inconclusive", true)]
    public async Task SummaryUsesFullCountsWhenOnlySomeDiagnosticsAreReturned(string outcome, bool raw)
    {
        using var server = ServerFor(CompletedRun(Enumerable.Repeat(outcome, 205).ToArray()));
        var result = await Verify(server, raw);

        Assert.Equal(1, result.ExitCode);
        if (raw)
        {
            var summary = JsonNode.Parse(result.Output)!;
            Assert.False(summary["ok"]!.GetValue<bool>());
            Assert.Equal(205, summary["tests"]![outcome]!.GetValue<int>());
            Assert.Equal(200, summary["tests"]!["failures"]!.AsArray().Count);
            Assert.True(summary["tests"]!["truncated"]!.GetValue<bool>());
        }
        else
        {
            Assert.Contains($"205 {outcome}", result.Output);
            Assert.Contains("test details truncated", result.Output + result.Error);
        }
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("failed", true)]
    [InlineData("inconclusive", false)]
    [InlineData("inconclusive", true)]
    [InlineData("unknown", false)]
    [InlineData("unknown", true)]
    public async Task NonSuccessDetailsStillFailWhenAggregateCountsAreMissing(string outcome, bool raw)
    {
        var run = CompletedRun([outcome]);
        run.Remove("failed");
        run.Remove("inconclusive");
        using var server = ServerFor(run);
        var result = await Verify(server, raw);

        Assert.Equal(1, result.ExitCode);
        if (raw)
        {
            var summary = JsonNode.Parse(result.Output)!;
            Assert.False(summary["ok"]!.GetValue<bool>());
            Assert.Single(summary["tests"]!["failures"]!.AsArray());
        }
        else
        {
            Assert.Contains(outcome == "inconclusive" ? "0 failed, 1 inconclusive" : "1 failed", result.Output);
            Assert.Contains("Case0", result.Output);
            Assert.Contains("diagnostic for Case0", result.Output);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedResultsDoNotInheritTruncationFromAnEarlierPoll(bool raw)
    {
        using var server = ServerFor(CompletedRun(["passed"]), earlierTruncatedPoll: true);
        var result = await Verify(server, raw);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, server.Requests.Count(request => request.Path == "/tools/test_results"));
        if (raw)
        {
            var summary = JsonNode.Parse(result.Output)!;
            Assert.True(summary["ok"]!.GetValue<bool>());
            Assert.Equal(1, summary["tests"]!["passed"]!.GetValue<int>());
            Assert.Equal(0, summary["tests"]!["failed"]!.GetValue<int>());
            Assert.False(summary["tests"]!["truncated"]!.GetValue<bool>());
        }
        else
        {
            Assert.Contains("1 passed, 0 failed", result.Output);
            Assert.DoesNotContain("test details truncated", result.Output + result.Error);
        }
    }

    // The CLI accepts jobs for tool calls in general. Current test_results runs on the worker
    // thread and returns inline; this covers that existing generic job contract, not a claim
    // that today's test_results handler defers its work.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredResultsKeepTheirCountsAndNestedTruncationMarker(bool raw)
    {
        var statuses = Enumerable.Repeat("skipped", 200).Append("failed").ToArray();
        using var server = ServerFor(CompletedRun(statuses), deferred: true);
        var result = await Verify(server, raw);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["/tools/test_run", "/tools/test_results", "/jobs/test-results-1", "/tools/console_read_logs"],
            server.Requests.Select(request => request.Path));
        if (raw)
        {
            var summary = JsonNode.Parse(result.Output)!;
            Assert.False(summary["ok"]!.GetValue<bool>());
            Assert.Equal(1, summary["tests"]!["failed"]!.GetValue<int>());
            Assert.True(summary["tests"]!["truncated"]!.GetValue<bool>());
        }
        else
        {
            Assert.Contains("1 failed", result.Output);
            Assert.Contains("test details truncated", result.Output + result.Error);
        }
    }

    private static JsonObject CompletedRun(string[] statuses)
    {
        var details = statuses.Select((status, index) => new JsonObject
        {
            ["name"] = $"Case{index}",
            ["fullName"] = $"Suite.Case{index}",
            ["status"] = status,
            ["message"] = $"diagnostic for Case{index}",
        }).Where(detail => detail["status"]!.GetValue<string>() != "passed").ToArray();

        return new JsonObject
        {
            ["status"] = "completed",
            ["mode"] = "EditMode",
            ["passed"] = statuses.Count(status => status == "passed"),
            ["failed"] = statuses.Count(status => status == "failed"),
            ["skipped"] = statuses.Count(status => status == "skipped"),
            ["inconclusive"] = statuses.Count(status => status == "inconclusive"),
            ["results"] = new JsonArray(details.Take(200).Cast<JsonNode>().ToArray()),
            ["truncated"] = details.Length > 200,
        };
    }

    private static FakeUnityServer ServerFor(JsonObject run, bool deferred = false, bool earlierTruncatedPoll = false)
    {
        var server = new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"started":true}}""");

        if (earlierTruncatedPoll)
        {
            // Current Unity snapshots collect details at completion. A server that supplies
            // partial details must still be allowed to finish with an untruncated answer;
            // the CLI's summary describes that final answer, not an earlier poll's metadata.
            server.Enqueue(200, """{"status":"success","result":{"status":"running","results":[]},"truncated":true}""");
        }

        if (deferred)
        {
            server.Enqueue(202, """{"status":"success","result":{"state":"running","jobId":"test-results-1"}}""")
                .Enqueue(200, new JsonObject
                {
                    ["status"] = "success",
                    ["result"] = new JsonObject
                    {
                        ["id"] = "test-results-1",
                        ["status"] = "completed",
                        ["result"] = run,
                    },
                }.ToJsonString());
        }
        else
        {
            // The REST envelope hoists the tool's truncation marker; a completed job keeps
            // that marker inside its nested result instead.
            var truncated = run["truncated"]!.GetValue<bool>();
            run.Remove("truncated");
            server.Enqueue(200, new JsonObject
            {
                ["status"] = "success",
                ["result"] = run,
                ["truncated"] = truncated,
            }.ToJsonString());
        }

        return server.Enqueue(200, """{"status":"success","result":{"logs":[]}}""");
    }

    private static async Task<(int ExitCode, string Output, string Error)> Verify(FakeUnityServer server, bool raw)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CommandContext
        {
            Out = output,
            Err = error,
            ReadDescriptors = () => [server.Descriptor()],
        };
        string[] arguments = raw
            ? ["verify", "--no-compile", "--test", "--raw"]
            : ["verify", "--no-compile", "--test"];
        var exitCode = await Program.Run(arguments, context);
        return (exitCode, output.ToString(), error.ToString());
    }
}

using System.Text.Json.Nodes;
using IsuzuUnityCli.Commands;
using IsuzuUnityCli.Tests.Fakes;
using Xunit;

namespace IsuzuUnityCli.Tests;

[Collection("environment")]
public sealed class VerifyTestOutcomeTests
{
    // test_results excludes passing tests before taking its limit, but skipped tests still
    // occupy that limit. A failure after 200 skipped cases must still fail verification.
    [Theory]
    [InlineData("failed", false)]
    [InlineData("inconclusive", false)]
    [InlineData("failed", true)]
    [InlineData("inconclusive", true)]
    public async Task AggregateOutcomeFailsEvenWhenAllReturnedDetailsAreSkipped(string outcome, bool raw)
    {
        var statuses = Enumerable.Repeat("skipped", 200).Append(outcome).ToArray();
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
            Assert.Equal(200, summary["tests"]!["skipped"]!.GetValue<int>());
            Assert.True(summary["tests"]!["truncated"]!.GetValue<bool>());
            Assert.Empty(summary["tests"]!["failures"]!.AsArray());
        }
        else
        {
            Assert.Contains(outcome == "failed" ? "1 failed" : "0 failed", result.Output);
            if (outcome == "inconclusive")
                Assert.Contains("1 inconclusive", result.Output);
            Assert.Contains("test details truncated", result.Output);
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
    [InlineData("failed")]
    [InlineData("inconclusive")]
    public async Task HumanSummaryCountsTheFullRunInsteadOfTheReturnedDiagnostics(string outcome)
    {
        using var server = ServerFor(CompletedRun(Enumerable.Repeat(outcome, 205).ToArray()));
        var result = await Verify(server, raw: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"205 {outcome}", result.Output);
        Assert.Contains("test details truncated", result.Output);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("inconclusive")]
    public async Task HumanSummaryKeepsVisibleFailuresWhenAggregateCountsAreMissing(string outcome)
    {
        var run = CompletedRun([outcome]);
        run.Remove("failed");
        run.Remove("inconclusive");
        using var server = ServerFor(run);
        var result = await Verify(server, raw: false);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(outcome == "inconclusive" ? "0 failed, 1 inconclusive" : "1 failed", result.Output);
        Assert.Contains("Case0: diagnostic for Case0", result.Output);
        Assert.DoesNotContain("test details truncated", result.Output);
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

    private static FakeUnityServer ServerFor(JsonObject run)
    {
        // REST lifts the tool's truncation marker into the envelope.
        var truncated = run["truncated"]!.GetValue<bool>();
        run.Remove("truncated");
        return new FakeUnityServer()
            .Enqueue(200, """{"status":"success","result":{"started":true}}""")
            .Enqueue(200, new JsonObject
            {
                ["status"] = "success",
                ["result"] = run,
                ["truncated"] = truncated,
            }.ToJsonString())
            .Enqueue(200, """{"status":"success","result":{"logs":[]}}""");
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

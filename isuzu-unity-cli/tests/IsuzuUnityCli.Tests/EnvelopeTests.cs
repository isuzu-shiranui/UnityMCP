using IsuzuUnityCli.Http;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class EnvelopeTests
{
    [Fact]
    public void SuccessExposesResult()
    {
        var envelope = Envelope.Parse(200, """{"status":"success","result":{"isPlaying":true}}""");

        Assert.False(envelope.IsError);
        Assert.True(envelope.Result!["isPlaying"]!.GetValue<bool>());
        Assert.False(envelope.Truncated);
        Assert.Null(envelope.Next);
    }

    [Fact]
    public void ErrorEnvelopeOnOkStatusSurfacesCodeAndMessage()
    {
        var envelope = Envelope.Parse(200, """{"status":"error","error":{"code":"tool_not_found","message":"No tool named 'x'."}}""");

        Assert.True(envelope.IsError);
        Assert.Equal("tool_not_found", envelope.ErrorCode);
        Assert.Equal("No tool named 'x'.", envelope.ErrorMessage);
    }

    [Fact]
    public void ClientErrorThrowsWithUnityMessage()
    {
        var e = Assert.Throws<UnityError>(() => Envelope.Parse(400, """{"status":"error","error":{"code":"bad_args","message":"limit must be positive"}}"""));

        Assert.Equal("bad_args", e.Code);
        Assert.Equal("limit must be positive", e.Message);
        Assert.Equal(400, e.HttpStatus);
    }

    [Fact]
    public void ClientErrorWithoutEnvelopeFallsBackToStatus()
    {
        var e = Assert.Throws<UnityError>(() => Envelope.Parse(404, "{}"));

        Assert.Equal("client_error", e.Code);
        Assert.Equal("HTTP 404", e.Message);
    }

    [Fact]
    public void NonJsonBodyIsReportedWithExcerpt()
    {
        var body = new string('x', 300);
        var e = Assert.Throws<UnityError>(() => Envelope.Parse(502, body));

        Assert.Equal("non_json", e.Code);
        Assert.Equal($"Unity returned a non-JSON response (HTTP 502): {body.Substring(0, 200)}", e.Message);
        Assert.Equal(502, e.HttpStatus);
    }

    [Fact]
    public void TruncatedAndNextPassThrough()
    {
        var envelope = Envelope.Parse(200, """{"status":"success","result":{"items":[]},"truncated":true,"next":{"offset":50}}""");

        Assert.True(envelope.Truncated);
        Assert.Equal(50, envelope.Next!["offset"]!.GetValue<int>());
        Assert.True(envelope.Raw["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void AcceptedIsANormalSuccess()
    {
        var envelope = Envelope.Parse(202, """{"status":"success","result":{"jobId":"j1","poll":"/jobs/j1","message":"queued"}}""");

        Assert.False(envelope.IsError);
        Assert.Equal(202, envelope.HttpStatus);
        Assert.Equal("j1", envelope.Result!["jobId"]!.GetValue<string>());
    }
}

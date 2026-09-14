using System.Text;
using IsuzuUnityCli.Cli;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class ToolArgumentsTests
{
    [Fact]
    public void JsonObjectIsTakenAsIs()
    {
        var parsed = ArgParser.Parse(["call", "x", "--json", """{"name":"Player","limit":5}"""]);
        var args = ToolArguments.Build("x", parsed);

        Assert.Equal("Player", args["name"]!.GetValue<string>());
        Assert.Equal(5, args["limit"]!.GetValue<int>());
    }

    [Fact]
    public void JsonArrayIsRejected()
    {
        var parsed = ArgParser.Parse(["call", "x", "--json", "[1,2]"]);
        var e = Assert.Throws<CliException>(() => ToolArguments.Build("x", parsed));

        Assert.Equal("--json must be a JSON object.", e.Message);
    }

    [Fact]
    public void InvalidJsonReportsTheParserMessage()
    {
        var parsed = ArgParser.Parse(["call", "x", "--json", "{not json"]);
        var e = Assert.Throws<CliException>(() => ToolArguments.Build("x", parsed));

        Assert.StartsWith("--json is not valid JSON: ", e.Message);
    }

    [Fact]
    public void IndividualOptionBeatsJson()
    {
        var parsed = ArgParser.Parse(["call", "x", "--json", """{"limit":5,"type":"log"}""", "--limit", "20"]);
        var args = ToolArguments.Build("x", parsed);

        Assert.Equal(20, args["limit"]!.GetValue<long>());
        Assert.Equal("log", args["type"]!.GetValue<string>());
    }

    [Fact]
    public void BareFlagsBecomeTrueAndCliOptionsAreDropped()
    {
        var parsed = ArgParser.Parse(["call", "x", "--include_warnings", "--raw", "--project", "P"]);
        var args = ToolArguments.Build("x", parsed);

        Assert.True(args["include_warnings"]!.GetValue<bool>());
        Assert.False(args.ContainsKey("raw"));
        Assert.False(args.ContainsKey("project"));
    }

    [Fact]
    public void FileIsBase64ForExecuteCode()
    {
        const string source = "var s = \"C:\\\\path\";\nreturn s;";
        var parsed = ArgParser.Parse(["call", "execute_code", "--file", "snippet.cs"]);
        var args = ToolArguments.Build("execute_code", parsed, path => path == "snippet.cs" ? source : throw new FileNotFoundException(path));

        Assert.False(args.ContainsKey("code"));
        Assert.Equal(source, Encoding.UTF8.GetString(Convert.FromBase64String(args["code_base64"]!.GetValue<string>())));
    }

    [Fact]
    public void FileIsPlainCodeForOtherTools()
    {
        var parsed = ArgParser.Parse(["call", "other", "--file", "snippet.cs"]);
        var args = ToolArguments.Build("other", parsed, _ => "contents");

        Assert.Equal("contents", args["code"]!.GetValue<string>());
        Assert.False(args.ContainsKey("code_base64"));
    }

    /// <summary>
    /// A name given both as a value and bare keeps the values.
    /// </summary>
    /// <remarks>
    /// A trailing '--type' has no word after it and reads as a flag, and the flags were applied
    /// last: the list built from the values before it was replaced by true, and the tool answered
    /// about an argument nobody typed.
    /// </remarks>
    [Fact]
    public void ABareNameDoesNotReplaceTheValuesGivenForIt()
    {
        var parsed = ArgParser.Parse(["call", "x", "--type", "error", "--type", "log", "--type"]);
        var args = ToolArguments.Build("x", parsed);

        Assert.Equal("[\"error\",\"log\"]", args["type"]!.ToJsonString());
    }

    /// <summary>A dotted name is a field of an object argument, split at the first dot only.</summary>
    /// <remarks>Serialized property paths carry dots of their own, and they are keys, not nesting.</remarks>
    [Fact]
    public void ADottedNameSetsOneFieldOfAnObject()
    {
        var parsed = ArgParser.Parse(["call", "x", "--values.m_LocalPosition.x", "2", "--values.speed", "45", "--values.label", "true"]);
        var args = ToolArguments.Build("x", parsed);

        Assert.Equal("{\"m_LocalPosition.x\":2,\"speed\":45,\"label\":true}", args["values"]!.ToJsonString());
    }

    [Fact]
    public void AnArgumentsFileWithAByteOrderMarkIsReadAndOptionsOverrideIt()
    {
        var parsed = ArgParser.Parse(["call", "x", "--args-file", "args.json", "--limit", "3"]);
        var args = ToolArguments.Build("x", parsed, _ => "﻿{\"m_Text\":\"true\",\"limit\":1}");

        Assert.Equal("true", args["m_Text"]!.GetValue<string>());
        Assert.Equal(3, args["limit"]!.GetValue<long>());
        Assert.False(args.ContainsKey("args-file"));
    }
}

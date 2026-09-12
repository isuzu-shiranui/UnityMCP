using System.Text.Json;

using IsuzuUnityCli.Cli;
using Xunit;

namespace IsuzuUnityCli.Tests;

public sealed class ScalarCoercionTests
{
    [Fact]
    public void BooleansAndNumbersBecomeTyped()
    {
        Assert.Equal(true, ScalarCoercion.Coerce("true"));
        Assert.Equal(false, ScalarCoercion.Coerce("false"));
        Assert.Equal(7L, ScalarCoercion.Coerce("007"));
        Assert.Equal(100000d, ScalarCoercion.Coerce("1e5"));
        Assert.Equal(16L, ScalarCoercion.Coerce("0x10"));
        Assert.Equal(1.5d, ScalarCoercion.Coerce("1.5"));
    }

    /// <summary>
    /// A whole number reaches the Editor with the digits that were typed.
    /// </summary>
    /// <remarks>
    /// An instance id on Unity 6.5 can exceed 2^53. Carried as a double, 568105589204596758 was
    /// sent as 568105589204596736 and the call came back not_found, naming an id nobody typed.
    /// </remarks>
    [Theory]
    [InlineData("568105589204596758")]
    [InlineData("-568105589204596758")]
    [InlineData("9223372036854775807")]
    public void ALargeWholeNumberKeepsItsDigits(string value)
    {
        Assert.Equal(value, ScalarCoercion.ToJsonNode(value).ToJsonString());
        Assert.Equal(long.Parse(value), ScalarCoercion.Coerce(value));
    }

    [Fact]
    public void EverythingElseStaysAString()
    {
        Assert.Equal("2Player", ScalarCoercion.Coerce("2Player"));
        Assert.Equal("", ScalarCoercion.Coerce(""));
        Assert.Equal("   ", ScalarCoercion.Coerce("   "));
        Assert.Equal("error", ScalarCoercion.Coerce("error"));
        Assert.Equal("Infinity", ScalarCoercion.Coerce("Infinity"));
    }

    [Fact]
    public void JsonNodeCarriesTheCoercedType()
    {
        Assert.Equal("16", ScalarCoercion.ToJsonNode("0x10").ToJsonString());
        Assert.Equal("true", ScalarCoercion.ToJsonNode("true").ToJsonString());
        Assert.Equal("\"2Player\"", ScalarCoercion.ToJsonNode("2Player").ToJsonString());
    }

    /// <summary>
    /// An argument that takes a list or an object gets one.
    /// </summary>
    /// <remarks>
    /// A tool taking several paths at once has no other way to be called from a command line:
    /// sent as a string, the whole line arrived as one path and the tool answered about a type
    /// named after the opening bracket.
    /// </remarks>
    [Fact]
    public void AnArrayOrObjectArrivesAsOne()
    {
        Assert.Equal("[\"a\",\"b\"]", ScalarCoercion.ToJsonNode("[\"a\",\"b\"]").ToJsonString());
        Assert.Equal("{\"x\":1}", ScalarCoercion.ToJsonNode("{\"x\":1}").ToJsonString());
        Assert.Equal("[1,[2]]", ScalarCoercion.ToJsonNode(" [1,[2]] ").ToJsonString());
    }

    /// <summary>Text that only looks like the start of JSON stays the string it was typed as.</summary>
    [Theory]
    [InlineData("[unclosed")]
    [InlineData("[Header] Stats")]
    [InlineData("{ not closed")]
    public void TextThatDoesNotParseIsStillAString(string value)
    {
        var node = ScalarCoercion.ToJsonNode(value);

        Assert.Equal(JsonValueKind.String, node.GetValueKind());
        Assert.Equal(value, node.GetValue<string>());
    }

    /// <summary>
    /// A bracketed list that does not parse is refused rather than sent on as one long string.
    /// </summary>
    /// <remarks>
    /// Windows PowerShell removes the double quotes from an argument on its way to a native
    /// program, so ["a","b"] arrives as [a,b]. Passed through as a string it reached the Editor
    /// as a single path named after the whole line, and the reply was an error about a type
    /// called '["a' - which sends the reader after the wrong thing entirely.
    /// </remarks>
    [Theory]
    [InlineData("[UnityEngine.Time/frameCount,UnityEngine.Application/isPlaying]")]
    [InlineData("{paths:[a,b]}")]
    public void SomethingShapedLikeJsonThatDoesNotParseIsRefused(string value)
    {
        var thrown = Assert.Throws<CliException>(() => ScalarCoercion.ToJsonNode(value));

        Assert.Contains("--paths one --paths two", thrown.Message);
    }

    /// <summary>
    /// A C# snippet reaches execute_code whatever punctuation it holds.
    /// </summary>
    /// <remarks>
    /// The separator that tells a quote-stripped list apart from a value is a colon inside braces
    /// and a comma inside brackets, and ordinary C# has both: a ternary, a case label, a Windows
    /// path, an interpolated format specifier. Refused, they came back advised to name the option
    /// once per value, which has nothing to do with a snippet.
    /// </remarks>
    [Theory]
    [InlineData("{ return Application.isPlaying ? 1 : 0; }")]
    [InlineData("{ var p = \"C:/tmp/a.png\"; return p; }")]
    [InlineData("{ switch (n) { case 1: return 1; } return 0; }")]
    [InlineData("{ Debug.Log($\"pos: {t.position}\"); }")]
    [InlineData("[SerializeField, Range(0,1)]")]
    public void CodeIsNotAStrippedList(string value)
    {
        var node = ScalarCoercion.ToJsonNode(value);

        Assert.Equal(JsonValueKind.String, node.GetValueKind());
        Assert.Equal(value, node.GetValue<string>());
    }

    /// <summary>
    /// A value that merely wears brackets is a value, not a mangled list.
    /// </summary>
    /// <remarks>
    /// Refusing everything that opens and closes like JSON took a GameObject named [Player] and a
    /// C# block bound for execute_code with it, and left no spelling that could reach either. The
    /// separator is what a stripped-quote list always has and these never do.
    /// </remarks>
    [Theory]
    [InlineData("[Player]")]
    [InlineData("[SYSTEM]")]
    [InlineData("{ var x = 1; return x; }")]
    public void BracketsWithNothingSeparatedInsideThemStayAString(string value)
    {
        var node = ScalarCoercion.ToJsonNode(value);

        Assert.Equal(JsonValueKind.String, node.GetValueKind());
        Assert.Equal(value, node.GetValue<string>());
    }
}

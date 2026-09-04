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
        Assert.Equal(7d, ScalarCoercion.Coerce("007"));
        Assert.Equal(100000d, ScalarCoercion.Coerce("1e5"));
        Assert.Equal(16d, ScalarCoercion.Coerce("0x10"));
        Assert.Equal(1.5d, ScalarCoercion.Coerce("1.5"));
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
}

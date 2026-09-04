using System.Globalization;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Cli;

/// <summary>
/// Turns command-line text into the JSON scalar JavaScript's <c>Number()</c> would have produced,
/// so <c>--raw</c> output shows what was actually sent.
/// </summary>
public static class ScalarCoercion
{
    /// <summary>Returns a <see cref="bool"/>, a <see cref="double"/>, or the original string.</summary>
    public static object Coerce(string value)
    {
        if (value == "true")
        {
            return true;
        }

        if (value == "false")
        {
            return false;
        }

        if (TryParseNumber(value, out var number))
        {
            return number;
        }

        return value;
    }

    public static JsonNode ToJsonNode(string value)
    {
        return Coerce(value) switch
        {
            bool b => JsonValue.Create(b),
            double d => JsonValue.Create(d),
            _ => JsonValue.Create(value),
        };
    }

    private static bool TryParseNumber(string value, out double number)
    {
        number = 0;
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        // Number("0x10") is 16; double.TryParse has no hex form, so the prefix is handled by hand.
        if (trimmed.Length > 2 && trimmed[0] == '0' && (trimmed[1] == 'x' || trimmed[1] == 'X'))
        {
            if (long.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
            {
                number = hex;
                return true;
            }

            return false;
        }

        // Infinity and NaN have no JSON representation, so they stay strings even though Number() accepts them.
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return false;
        }

        number = parsed;
        return true;
    }
}

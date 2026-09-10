using System.Globalization;
using System.Text.Json;
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

    /// <summary>
    /// The same coercion, plus the arrays and objects a tool argument can be.
    /// </summary>
    /// <remarks>
    /// A command line has no other way to type one. Without this, <c>--paths '["a","b"]'</c>
    /// reached the Editor as a single path whose name was that whole line, and the tool answered
    /// about a type called <c>["a</c> rather than saying it had been handed something odd. Only
    /// text that both starts like JSON and parses as JSON is taken as JSON, so an unbalanced
    /// bracket stays the string it was typed as.
    /// </remarks>
    public static JsonNode ToJsonNode(string value)
    {
        if (TryStructured(value, out var structured))
        {
            return structured;
        }

        return Coerce(value) switch
        {
            bool b => JsonValue.Create(b),
            double d => JsonValue.Create(d),
            _ => JsonValue.Create(value),
        };
    }

    private static bool TryStructured(string value, out JsonNode node)
    {
        node = null!;
        var trimmed = value.Trim();

        if (trimmed.Length == 0 || (trimmed[0] != '[' && trimmed[0] != '{'))
        {
            return false;
        }

        try
        {
            var parsed = JsonNode.Parse(value);

            if (parsed is JsonArray or JsonObject)
            {
                node = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        // Windows PowerShell strips the double quotes out of an argument on its way to a native
        // program, so ["a","b"] arrives as [a,b]. Sent on as a string, that reached the Editor as
        // a single path named after the whole line and came back as an error about a type called
        // '["a' - a wrong answer wearing the shape of a real one.
        var closed = trimmed[0] == '[' ? trimmed[^1] == ']' : trimmed[^1] == '}';

        if (closed)
        {
            throw new CliException(
                $"'{trimmed}' opens and closes like JSON but does not parse. Windows PowerShell "
                + "removes the double quotes from an argument passed to a native program, which "
                + "leaves exactly this. Escape them: --paths '[\\\"one\\\",\\\"two\\\"]'.");
        }

        return false;
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

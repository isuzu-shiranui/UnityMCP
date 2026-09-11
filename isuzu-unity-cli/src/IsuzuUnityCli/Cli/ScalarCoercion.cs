using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsuzuUnityCli.Cli;

/// <summary>
/// Turns command-line text into the JSON scalar it spells, so <c>--raw</c> output shows what was
/// actually sent.
/// </summary>
public static class ScalarCoercion
{
    /// <summary>
    /// Returns a <see cref="bool"/>, a <see cref="long"/>, a <see cref="double"/>, or the original
    /// string.
    /// </summary>
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
            long l => JsonValue.Create(l),
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
        //
        // The separator is what tells that apart from a value that merely wears brackets. An
        // object named [Player] and a C# block passed to execute_code both open and close like
        // JSON and are not JSON, and refusing them left no way to name them at all.
        var closed = trimmed[0] == '['
            ? trimmed[^1] == ']' && trimmed.Contains(',')
            : trimmed[^1] == '}' && trimmed.Contains(':');

        if (closed && !ReadsAsCode(trimmed))
        {
            throw new CliException(
                $"'{trimmed}' opens and closes like JSON but does not parse. Windows PowerShell "
                + "removes the double quotes from an argument passed to a native program, which "
                + "leaves exactly this. Name the option once per value instead, which needs no "
                + "quotes at all: --paths one --paths two.");
        }

        return false;
    }

    /// <summary>
    /// Text that cannot be a JSON document with its quotes removed.
    /// </summary>
    /// <remarks>
    /// PowerShell removes the quotes and nothing else, so what arrives holds only what was inside
    /// the JSON: names, separators and brackets. None of these characters can be there, and every
    /// one of them is ordinary in a C# snippet - a ternary, a statement, an interpolation, a
    /// Windows path - which is the value execute_code is handed on the command line.
    /// </remarks>
    private static bool ReadsAsCode(string trimmed) =>
        trimmed.AsSpan().IndexOfAny(";()=$?") >= 0;

    private static bool TryParseNumber(string value, out object number)
    {
        number = null!;
        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        // Number("0x10") is 16; the numeric parsers have no hex form, so the prefix is read by hand.
        if (trimmed.Length > 2 && trimmed[0] == '0' && (trimmed[1] == 'x' || trimmed[1] == 'X'))
        {
            if (long.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
            {
                number = hex;
                return true;
            }

            return false;
        }

        // A whole number keeps its digits. An instance id on Unity 6.5 can exceed 2^53, and a
        // double holds that only to the nearest even value: 568105589204596758 arrives as
        // 568105589204596736, which is a different object or none at all.
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            number = whole;
            return true;
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

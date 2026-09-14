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
            var position = 0;

            if (TryNumbers(trimmed, ref position, out var rebuilt) && position == trimmed.Length
                && rebuilt is JsonArray or JsonObject)
            {
                node = rebuilt;
                return true;
            }

            throw new CliException(
                $"'{trimmed}' opens and closes like JSON but does not parse. Windows PowerShell "
                + "removes the double quotes from an argument passed to a native program, which "
                + "leaves exactly this, and a word without its quotes cannot say whether it was "
                + "a string. Name each field instead, which needs no quotes: --values.speed 45 "
                + "--paths one --paths two, or put the arguments in a JSON file: --args-file args.json.");
        }

        return false;
    }

    /// <summary>
    /// Reads JSON whose double quotes a shell removed, when nothing in it depended on them.
    /// </summary>
    /// <remarks>
    /// Only numbers come through the loss unchanged. <c>"true"</c> and <c>true</c>, or
    /// <c>["a,b"]</c> and <c>["a","b"]</c>, arrive as the same text, so any word as a value fails
    /// the read and the caller is refused rather than guessed for. A key needs no quotes to be
    /// read, and ends at its first colon.
    /// </remarks>
    private static bool TryNumbers(string text, ref int position, out JsonNode? node)
    {
        node = null;
        SkipSpace(text, ref position);

        if (position >= text.Length)
        {
            return false;
        }

        if (text[position] == '[')
        {
            var array = new JsonArray();
            position++;
            SkipSpace(text, ref position);

            if (position < text.Length && text[position] == ']')
            {
                position++;
                node = array;
                return true;
            }

            while (true)
            {
                if (!TryNumbers(text, ref position, out var item))
                {
                    return false;
                }

                array.Add(item);
                SkipSpace(text, ref position);

                if (position >= text.Length)
                {
                    return false;
                }

                if (text[position++] == ']')
                {
                    node = array;
                    return true;
                }

                if (text[position - 1] != ',')
                {
                    return false;
                }
            }
        }

        if (text[position] == '{')
        {
            var obj = new JsonObject();
            position++;

            while (true)
            {
                var colon = text.IndexOf(':', position);

                if (colon < 0)
                {
                    return false;
                }

                var key = text.Substring(position, colon - position).Trim();

                if (key.Length == 0 || key.AsSpan().IndexOfAny("{}[],") >= 0 || obj.ContainsKey(key))
                {
                    return false;
                }

                position = colon + 1;

                if (!TryNumbers(text, ref position, out var value))
                {
                    return false;
                }

                obj[key] = value;
                SkipSpace(text, ref position);

                if (position >= text.Length)
                {
                    return false;
                }

                if (text[position++] == '}')
                {
                    node = obj;
                    return true;
                }

                if (text[position - 1] != ',')
                {
                    return false;
                }
            }
        }

        var start = position;

        while (position < text.Length && "+-.0123456789eE".IndexOf(text[position]) >= 0)
        {
            position++;
        }

        if (position == start || !TryParseNumber(text.Substring(start, position - start), out var number))
        {
            return false;
        }

        node = number is long whole ? JsonValue.Create(whole) : JsonValue.Create((double)number);
        return true;
    }

    private static void SkipSpace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
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

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Builds the untyped parameter object the handlers under <c>Handlers/</c> take.
    /// </summary>
    /// <remarks>
    /// A <c>[McpTool]</c> method carries a typed signature so the catalog can derive its
    /// schema, while the handler it calls reads its values out of a <c>JObject</c>. This
    /// helper is the seam between the two.
    /// <para>
    /// Null values are dropped rather than written as JSON null, because those handlers
    /// distinguish "absent, use my default" from "explicitly null" and would take the latter
    /// as a real value.
    /// </para>
    /// </remarks>
    internal static class ToolArgs
    {
        public static JObject Of(params (string Key, object Value)[] pairs)
        {
            var args = new JObject();

            foreach (var (key, value) in pairs)
            {
                if (value == null)
                {
                    continue;
                }

                args[key] = value as JToken ?? JToken.FromObject(value);
            }

            return args;
        }
    }
}

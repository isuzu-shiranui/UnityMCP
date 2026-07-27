using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Builds the untyped parameter object the pre-v3 handlers still expect.
    /// </summary>
    /// <remarks>
    /// The <c>[McpTool]</c> wrappers exist to give each operation a typed signature the
    /// catalog can derive a schema from; their bodies still delegate to the original handler
    /// implementations so behaviour is unchanged during the migration. This helper is the
    /// seam between the two.
    /// <para>
    /// Null values are dropped rather than written as JSON null, because the legacy handlers
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

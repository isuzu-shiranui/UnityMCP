using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Handlers
{
    internal static class StepChangeLog
    {
        public static JObject Append(JObject previous, JToken value, int frame, double time, string error = null)
        {
            if (previous?["error"] != null) return (JObject)previous.DeepClone();
            if (error != null) return new JObject { ["error"] = error };
            value = value?.DeepClone() ?? JValue.CreateNull();
            if (previous == null) return new JObject { ["start"] = value.DeepClone(), ["changes"] = new JArray(), ["end"] = value };
            var next = (JObject)previous.DeepClone();
            if (!JToken.DeepEquals(next["end"], value))
            {
                var entries = (JArray)next["changes"];
                var entry = new JArray(frame, time, value.DeepClone());
                if (entries.Count < 200) entries.Add(entry);
                else { entries[199] = entry; next["truncated"] = true; }
            }
            next["end"] = value;
            return next;
        }
    }
}

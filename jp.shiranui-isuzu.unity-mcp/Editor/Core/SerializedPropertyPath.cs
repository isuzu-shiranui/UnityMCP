using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace UnityMCP.Editor.Core
{
    internal static class SerializedPropertyPath
    {
        public static SerializedProperty Find(SerializedObject serialized, string path, out string error)
        {
            error = null;
            var exact = serialized.FindProperty(path);
            if (exact != null) return exact;

            var parts = path.Split('.');
            SerializedProperty parent = null;
            for (var index = 0; index < parts.Length; index++)
            {
                if (parent != null)
                {
                    var remainder = parent.FindPropertyRelative(string.Join(".", parts.Skip(index)));
                    if (remainder != null) return remainder;
                }
                var segment = parts[index];
                if (parent != null && parent.isArray && segment == "Array" && index + 1 < parts.Length
                    && (parts[index + 1] == "size" || parts[index + 1].StartsWith("data[", StringComparison.Ordinal)))
                    segment += "." + parts[++index];
                var direct = parent == null ? serialized.FindProperty(segment) : parent.FindPropertyRelative(segment);
                if (direct != null)
                {
                    parent = direct;
                    continue;
                }

                var children = Children(serialized, parent).ToArray();
                var matches = children.Where(p => string.Equals(Normalize(p.name), Normalize(segment), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1)
                {
                    parent = serialized.FindProperty(matches[0].path);
                    continue;
                }

                var candidates = (matches.Length > 1 ? matches : children)
                    .OrderBy(p => Distance(Normalize(segment), Normalize(p.name)))
                    .ThenBy(p => p.path, StringComparer.Ordinal).Take(5).Select(p => p.path).ToArray();
                error = $"Property '{path}' {(matches.Length > 1 ? "is ambiguous" : "was not found")} on '{serialized.targetObject.GetType().FullName}'. "
                    + "Serialized path candidates: " + (candidates.Length == 0 ? "(none)" : string.Join(", ", candidates)) + ".";
                return null;
            }
            return parent;
        }

        // Walk only immediate children: descending through every array or managed reference can
        // make a miss scan an entire asset, including cyclic managed reference graphs.
        private static IEnumerable<(string name, string path)> Children(SerializedObject serialized, SerializedProperty parent)
        {
            using var iterator = parent == null ? serialized.GetIterator() : parent.Copy();
            var depth = parent == null ? 0 : parent.depth + 1;
            if (!iterator.Next(true)) yield break;
            do
            {
                if (iterator.depth != depth) yield break;
                yield return (iterator.name, iterator.propertyPath);
            } while (iterator.Next(false));
        }

        private static string Normalize(string name) => name.StartsWith("m_", StringComparison.Ordinal) ? name.Substring(2) : name;

        private static int Distance(string left, string right)
        {
            var costs = Enumerable.Range(0, right.Length + 1).ToArray();
            for (var i = 1; i <= left.Length; i++)
            {
                var previous = costs[0];
                costs[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    var old = costs[j];
                    costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), previous
                        + (char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1));
                    previous = old;
                }
            }
            return costs[right.Length];
        }
    }
}

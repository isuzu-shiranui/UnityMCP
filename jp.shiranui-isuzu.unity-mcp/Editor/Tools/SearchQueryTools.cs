using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// The Editor's own search, as one question instead of a walk and a read for each result.
    /// </summary>
    /// <remarks>
    /// Reached by name rather than compiled against. Unity's Search API is not on every version
    /// this package supports, and a direct reference would stop the package building on the ones
    /// without it rather than leaving the one tool unavailable.
    /// </remarks>
    internal static class SearchQueryTools
    {
        private static readonly Type ServiceType =
            Type.GetType("UnityEditor.Search.SearchService, UnityEditor");

        private static readonly Type FlagsType =
            Type.GetType("UnityEditor.Search.SearchFlags, UnityEditor");

        [McpTool(
            "search_query",
            "Ask the Editor's own search one question instead of walking the hierarchy and " +
            "reading each object: 'h:' searches the open scenes, 'p:' the project, 't:' filters " +
            "by type, 'ref:' finds what points at something, and 'p(name)' compares a serialized " +
            "property, so 'h: t:meshrenderer p(castshadows)!=\"Off\"' answers in one call what " +
            "scene_browse_hierarchy plus an inspect_read for each result answers in many. " +
            "Combine terms with and, or and a leading '-' to negate. Scene results carry the " +
            "object path the gameobject_ and inspect_ tools take. This is Unity's query " +
            "language, not one this package defines; a term Unity does not understand narrows " +
            "nothing rather than failing, so check the count against what you expected. A " +
            "project query can also answer with nothing the first time it is asked in an Editor " +
            "session and with the results on the next: 'p: t:Material' read 0 and then 2 in a " +
            "project holding two. Ask again before concluding a project query found nothing.",
            Idempotency = McpIdempotency.Safe,
            Group = McpToolGroups.Diagnostics,
            MaxResultSizeChars = 60000)]
        public static JObject Query(
            [McpArg("query", "The search query, e.g. 'h: t:light' or 'p: t:material shader:Lit'.",
                    Required = true)]
            string query = null,
            [McpArg("limit", "Maximum results to return.")]
            int limit = 50)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'query' is required, e.g. 'h: t:light' for every Light in the open scenes.");
            }

            if (limit < 1)
            {
                throw new McpToolException("invalid_params", "'limit' has to be at least 1.");
            }

            if (ServiceType == null || FlagsType == null)
            {
                throw new McpToolException(
                    "not_supported",
                    "This Unity version has no Search API. Use scene_browse_hierarchy with a " +
                    "component filter, then inspect_read for the values.",
                    501);
            }

            var items = Run(query);
            var results = new JArray();

            foreach (var item in items.Take(limit))
            {
                results.Add(Describe(item));
            }

            return new JObject
            {
                ["query"] = query,
                ["results"] = results,
                ["count"] = results.Count,
                ["truncated"] = items.Count > results.Count,
                ["total"] = items.Count,
            };
        }

        /// <summary>Runs the query and waits for it, rather than handing back a list still filling.</summary>
        private static List<object> Run(string query)
        {
            var request = ServiceType.GetMethod(
                "Request",
                new[] { typeof(string), FlagsType });

            if (request == null)
            {
                throw new McpToolException(
                    "not_supported",
                    "This Unity version's Search API has no Request(string, SearchFlags).",
                    501);
            }

            // Synchronous, because the alternative is handing back a list that is still filling
            // and a count that means nothing.
            var synchronous = Enum.Parse(FlagsType, "Synchronous");

            try
            {
                var list = request.Invoke(null, new[] { query, synchronous });

                return list is System.Collections.IEnumerable sequence
                    ? sequence.Cast<object>().Where(i => i != null).ToList()
                    : new List<object>();
            }
            catch (Exception e)
            {
                var reason = e.InnerException?.Message ?? e.Message;

                throw new McpToolException(
                    "invalid_params",
                    $"Search could not run '{query}': {reason}");
            }
        }

        /// <summary>One result, with the path the other tools take where there is one.</summary>
        private static JObject Describe(object item)
        {
            var type = item.GetType();
            var described = new JObject
            {
                ["id"] = Read(type, item, "id") as string,
            };

            // Only when the provider filled it in. It is null for a scene object, whose name is
            // already the last segment of the path below.
            var label = Read(type, item, "label") as string;

            if (!string.IsNullOrEmpty(label))
            {
                described["label"] = label;
            }

            var description = Read(type, item, "description") as string;

            if (!string.IsNullOrEmpty(description))
            {
                described["description"] = description;
            }

            var provider = Read(type, item, "provider");

            if (provider != null)
            {
                described["provider"] = Read(provider.GetType(), provider, "id") as string;
            }

            var target = ToObject(type, item);

            if (target != null)
            {
                described["type"] = target.GetType().Name;
                described["instanceId"] = EntityIdCompat.WireIdOf(target);

                var go = target as GameObject ?? (target as Component)?.gameObject;

                if (go != null && go.scene.IsValid())
                {
                    described["objectPath"] = Tools.ObjectResolve.PathOf(go);
                }
                else
                {
                    var asset = AssetDatabase.GetAssetPath(target);

                    if (!string.IsNullOrEmpty(asset))
                    {
                        described["assetPath"] = asset;
                    }
                }
            }

            return described;
        }

        private static UnityEngine.Object ToObject(Type type, object item)
        {
            try
            {
                // Chosen out of the overloads rather than asked for by signature: ToObject and
                // ToObject<T> both take no arguments, and naming an empty parameter list matches
                // them both and throws rather than picking one.
                var method = type.GetMethods()
                    .FirstOrDefault(m => m.Name == "ToObject"
                                         && !m.IsGenericMethod
                                         && m.GetParameters().Length == 0);

                return method?.Invoke(item, null) as UnityEngine.Object;
            }
            catch (Exception)
            {
                // A provider that cannot materialise its result still has a usable label and id,
                // and losing the whole row over it would hide the match.
                return null;
            }
        }

        private static object Read(Type type, object instance, string member)
        {
            try
            {
                var property = type.GetProperty(member);

                if (property != null)
                {
                    return property.GetValue(instance);
                }

                return type.GetField(member)?.GetValue(instance);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

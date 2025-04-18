using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityMCP.Editor.Core;

namespace UnityMCPHandlerSamples.Editor
{

    /// <summary>
    /// Command handler for searching assets in the Unity project.
    /// </summary>
    internal sealed class AssetSearchCommandHandler : IMcpCommandHandler
    {
        /// <summary>
        /// Gets the command prefix for this handler.
        /// </summary>
        public string CommandPrefix => "asset";

        /// <summary>
        /// Gets the description of this command handler.
        /// </summary>
        public string Description => "Search and retrieve assets from the project";

        /// <summary>
        /// Executes the command with the given parameters.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="parameters">The parameters for the command.</param>
        /// <returns>A JSON object containing the execution result.</returns>
        public JObject Execute(string action, JObject parameters)
        {
            return action.ToLower() switch
            {
                "search" => this.SearchAssets(parameters),
                "findbyname" => this.FindAssetsByName(parameters),
                "findbytype" => this.FindAssetsByType(parameters),
                _ => new JObject { ["success"] = false, ["error"] = $"Unknown action: {action}. Supported actions are 'search', 'findbyname', and 'findbytype'." }
            };
        }

        /// <summary>
        /// Searches for assets using the provided query.
        /// </summary>
        /// <param name="parameters">The search parameters.</param>
        /// <returns>A JSON object containing the search results.</returns>
        private JObject SearchAssets(JObject parameters)
        {
            var query = parameters["query"]?.ToString();
            if (string.IsNullOrWhiteSpace(query))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "Query parameter is required"
                };
            }

            var limit = parameters["limit"]?.Value<int>() ?? 100;
            var results = AssetDatabase.FindAssets(query);

            return this.FormatSearchResults(results, limit);
        }

        /// <summary>
        /// Finds assets by name.
        /// </summary>
        /// <param name="parameters">The search parameters.</param>
        /// <returns>A JSON object containing the search results.</returns>
        private JObject FindAssetsByName(JObject parameters)
        {
            var name = parameters["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "Name parameter is required"
                };
            }

            var limit = parameters["limit"]?.Value<int>() ?? 100;
            var exact = parameters["exact"]?.Value<bool>() ?? false;

            // Build the search query for name search
            var query = exact ? name : $"{name}";
            var results = AssetDatabase.FindAssets(query);

            // If exact match is requested, we need to filter the results
            if (exact)
            {
                var exactResults = new List<string>();
                foreach (var guid in results)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var assetName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                    if (string.Equals(assetName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        exactResults.Add(guid);
                    }
                }

                results = exactResults.ToArray();
            }

            return this.FormatSearchResults(results, limit);
        }

        /// <summary>
        /// Finds assets by type.
        /// </summary>
        /// <param name="parameters">The search parameters.</param>
        /// <returns>A JSON object containing the search results.</returns>
        private JObject FindAssetsByType(JObject parameters)
        {
            var typeName = parameters["type"]?.ToString();
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "Type parameter is required"
                };
            }

            var limit = parameters["limit"]?.Value<int>() ?? 100;

            // Build the search query for type search
            var query = $"t:{typeName}";
            var results = AssetDatabase.FindAssets(query);

            return this.FormatSearchResults(results, limit);
        }

        /// <summary>
        /// Formats the search results as a JSON object.
        /// </summary>
        /// <param name="guids">The GUIDs of the found assets.</param>
        /// <param name="limit">The maximum number of results to return.</param>
        /// <returns>A JSON object containing the formatted search results.</returns>
        private JObject FormatSearchResults(string[] guids, int limit)
        {
            var results = new JArray();
            var count = Math.Min(guids.Length, limit);

            for (int i = 0; i < count; i++)
            {
                var guid = guids[i];
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);

                if (string.IsNullOrEmpty(assetPath))
                {
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                if (asset == null)
                {
                    continue;
                }

                var assetName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                var assetType = asset.GetType().Name;

                results.Add(new JObject
                {
                    ["guid"] = guid,
                    ["path"] = assetPath,
                    ["name"] = assetName,
                    ["type"] = assetType
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["count"] = results.Count,
                ["total"] = guids.Length,
                ["results"] = results
            };
        }
    }
}

using System;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Finding and rearranging project assets.
    /// </summary>
    /// <remarks>
    /// Deletion goes to the trash rather than <c>AssetDatabase.DeleteAsset</c>. The alternative
    /// was to declare the tool destructive and demand a confirmation on every call, which costs
    /// a round trip every time and still loses the file when someone confirms the wrong one.
    /// Recoverable beats gated.
    /// </remarks>
    internal static class AssetTools
    {
        [McpTool(
            "asset_find",
            "Search the project for assets. Combine a type filter with a folder to keep the search " +
            "meaningful — an unfiltered search matches every asset in the project, which is rarely " +
            "the question. The reply is capped at limit and carries the full match count in total, " +
            "so a broad search is slow and uninformative rather than large.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Find(
            [McpArg("type", "Unity type to filter by, e.g. Material, Texture2D, MonoScript, Prefab.")]
            string type = null,
            [McpArg("name", "Name fragment to match.")]
            string name = null,
            [McpArg("folder", "Restrict to this folder, e.g. Assets/Art.")]
            string folder = null,
            [McpArg("label", "Restrict to assets carrying this label.")]
            string label = null,
            [McpArg("limit", "Maximum results to return.")]
            int limit = 50,
            [McpArg("offset", "Results to skip, for paging.")]
            int offset = 0)
        {
            var terms = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrWhiteSpace(name))
            {
                terms.Add(name);
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                terms.Add($"t:{type}");
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                terms.Add($"l:{label}");
            }

            string[] guids;

            if (string.IsNullOrWhiteSpace(folder))
            {
                guids = AssetDatabase.FindAssets(string.Join(" ", terms));
            }
            else
            {
                var normalised = folder.Replace('\\', '/').TrimEnd('/');

                if (!AssetDatabase.IsValidFolder(normalised))
                {
                    throw new McpToolException(
                        "not_found",
                        $"'{folder}' is not a folder in this project. Folder paths start at 'Assets/'.");
                }

                guids = AssetDatabase.FindAssets(string.Join(" ", terms), new[] { normalised });
            }

            var total = guids.Length;
            var page = guids.Skip(Math.Max(offset, 0)).Take(Math.Max(limit, 0)).ToArray();

            var results = new JArray(page.Select(guid =>
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);

                return (object)new JObject
                {
                    ["path"] = path,
                    ["guid"] = guid,
                    ["type"] = AssetDatabase.GetMainAssetTypeAtPath(path)?.Name,
                };
            }).ToArray());

            return new JObject
            {
                ["total"] = total,
                ["assets"] = results,
                ["truncated"] = offset + page.Length < total,
            };
        }

        [McpTool(
            "asset_info",
            "Describe one asset: its type, GUID, importer and labels. What it depends on is left " +
            "out unless include_dependencies is set.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Info(
            [McpArg("path", "Project path of the asset, e.g. Assets/Art/Wood.mat.")]
            string path = null,
            [McpArg("include_dependencies", "List the assets this one references.")]
            bool includeDependencies = false)
        {
            var asset = Require(path);
            var importer = AssetImporter.GetAtPath(path);

            var result = new JObject
            {
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
                ["type"] = AssetDatabase.GetMainAssetTypeAtPath(path)?.FullName,
                ["name"] = asset.name,
                ["isFolder"] = AssetDatabase.IsValidFolder(path),
                ["importer"] = importer == null ? null : (JToken)importer.GetType().Name,
                ["labels"] = new JArray(AssetDatabase.GetLabels(asset).Cast<object>().ToArray()),
            };

            if (includeDependencies)
            {
                result["dependencies"] = new JArray(
                    AssetDatabase.GetDependencies(path, false).Cast<object>().ToArray());
            }

            return result;
        }

        [McpTool(
            "asset_create_folder",
            "Create a folder under Assets, including any missing parents. Call this before writing " +
            "an asset into a folder that may not exist yet: the tools that write assets refuse a " +
            "path whose folder is missing rather than creating it. A folder that already exists is " +
            "not an error; the reply says created false.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject CreateFolder(
            [McpArg("path", "Folder to create, e.g. Assets/Art/Materials.")]
            string path = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var normalised = path.Replace('\\', '/').TrimEnd('/');

            if (!normalised.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{path}' is outside the project. Asset paths start at 'Assets/'.");
            }

            if (AssetDatabase.IsValidFolder(normalised))
            {
                return new JObject { ["path"] = normalised, ["created"] = false };
            }

            var segments = normalised.Split('/');
            var current = segments[0];

            for (var i = 1; i < segments.Length; i++)
            {
                var next = $"{current}/{segments[i]}";

                if (!AssetDatabase.IsValidFolder(next))
                {
                    var guid = AssetDatabase.CreateFolder(current, segments[i]);

                    if (string.IsNullOrEmpty(guid))
                    {
                        throw new McpToolException("tool_failed", $"Unity refused to create '{next}'.");
                    }
                }

                current = next;
            }

            return new JObject { ["path"] = normalised, ["created"] = true };
        }

        [McpTool(
            "asset_move",
            "Move or rename an asset, keeping its GUID so references survive.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Move(
            [McpArg("path", "Current project path of the asset.")]
            string path = null,
            [McpArg("destination", "New path, including the file name and extension.")]
            string destination = null)
        {
            Require(path);

            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new McpToolException("invalid_params", "'destination' is required.");
            }

            var target = destination.Replace('\\', '/');
            var parent = Path.GetDirectoryName(target)?.Replace('\\', '/');

            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                throw new McpToolException(
                    "not_found",
                    $"'{parent}' does not exist. Create it with asset_create_folder first.");
            }

            // Returns the reason as a string rather than throwing, and an empty string means it
            // worked — easy to mistake for a failure if the result is not checked.
            var error = AssetDatabase.MoveAsset(path, target);

            if (!string.IsNullOrEmpty(error))
            {
                throw new McpToolException("tool_failed", error);
            }

            return new JObject
            {
                ["from"] = path,
                ["path"] = target,
                ["guid"] = AssetDatabase.AssetPathToGUID(target),
            };
        }

        [McpTool(
            "asset_delete",
            "Move an asset to the OS trash, normally recoverable from there, so this does not ask " +
            "for confirmation. A folder path is accepted and takes everything under it, so check " +
            "what a path holds with asset_find before passing a directory.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Delete(
            [McpArg("path", "Project path of the asset to remove. A folder is accepted and removes " +
                            "the whole tree beneath it.")]
            string path = null)
        {
            Require(path);

            if (!AssetDatabase.MoveAssetToTrash(path))
            {
                throw new McpToolException(
                    "tool_failed",
                    $"Unity would not move '{path}' to the trash. It may be open, locked, or outside Assets/.");
            }

            return new JObject { ["deleted"] = true, ["path"] = path, ["recoverable"] = "OS trash" };
        }

        [McpTool(
            "asset_reimport",
            "Reimport an asset or a whole folder. Needed after editing a file on disk outside the " +
            "Editor, and after changing importer settings.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject Reimport(
            [McpArg("path", "Asset or folder to reimport.")]
            string path = null,
            [McpArg("recursive", "For a folder, reimport everything inside it.")]
            bool recursive = true)
        {
            Require(path);

            var options = ImportAssetOptions.ForceUpdate;

            if (recursive && AssetDatabase.IsValidFolder(path))
            {
                options |= ImportAssetOptions.ImportRecursive;
            }

            AssetDatabase.ImportAsset(path, options);

            return new JObject
            {
                ["path"] = path,
                ["reimported"] = true,
                ["note"] = "Reimporting a script does not recompile it; use compile_request for that.",
            };
        }

        private static UnityEngine.Object Require(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "'path' is required.");
            }

            var normalised = path.Replace('\\', '/');
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(normalised);

            if (asset == null)
            {
                throw new McpToolException(
                    "not_found",
                    $"No asset at '{path}'. Paths are project-relative and start at 'Assets/' or " +
                    "'Packages/'; asset_find will give you exact ones.");
            }

            return asset;
        }
    }
}

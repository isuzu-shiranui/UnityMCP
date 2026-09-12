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
        /// <summary>Dependencies listed. A production scene points at thousands.</summary>
        private const int MaxDependencies = 200;

        /// <summary>A typed load returning null does not mean the destination is empty.</summary>
        internal static void RefuseIncompatibleAsset(string path, UnityEngine.Object typedAsset)
        {
            if (typedAsset == null && (AssetDatabase.LoadMainAssetAtPath(path) != null
                || File.Exists(path) || Directory.Exists(path)))
            {
                throw new McpToolException("invalid_params",
                    $"'{path}' already exists but is not a compatible asset. Nothing was replaced.");
            }
        }

        [McpTool(
            "asset_find",
            "Search the project for assets. Combine a type filter with a folder to keep the search " +
            "meaningful — an unfiltered search matches every asset in the project, which is rarely " +
            "the question. The reply is capped at limit and carries the full match count in total, " +
            "so a broad search is slow and uninformative rather than large. With no folder the " +
            "search covers the installed packages as well as Assets, and they outnumber a small " +
            "project's own files heavily: one answered 4,384 with no folder and 6 for " +
            "folder 'Assets'. Pass folder 'Assets' whenever the question is about this project's " +
            "own content.",
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
            [McpArg("path", "Project path of the asset, e.g. Assets/Art/Wood.mat.", Required = true)]
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
                // Not recursive, but a scene's direct references are every material, texture,
                // prefab and mesh its objects point at.
                var dependencies = AssetDatabase.GetDependencies(path, false);

                result["dependencies"] = new JArray(
                    dependencies.Take(MaxDependencies).Cast<object>().ToArray());
                result["dependencyCount"] = dependencies.Length;

                if (dependencies.Length > MaxDependencies)
                {
                    result["truncated"] = $"{dependencies.Length} dependencies";
                }
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
            [McpArg("path", "Folder to create, e.g. Assets/Art/Materials.", Required = true)]
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
            [McpArg("path", "Current project path of the asset.", Required = true)]
            string path = null,
            [McpArg("destination", "New path, including the file name and extension.", Required = true)]
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
                            "the whole tree beneath it.", Required = true)]
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
            [McpArg("path", "Asset or folder to reimport.", Required = true)]
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

        [McpTool(
            "asset_export_package",
            "Export assets to a .unitypackage. Unity writes each asset together with its .meta, so " +
            "importing the file back restores the GUIDs and the references that pointed at them " +
            "survive. That makes this the rollback point to take before a bulk edit that rewrites " +
            "materials, re-slices sprites or rewrites import settings, on a project with no version " +
            "control or one whose assets are not committed. It is also how to hand part of a project " +
            "to someone who does not have the repository. The file lands outside the project, so " +
            "'destination' has to be stated rather than guessed, and one that is already there is " +
            "kept unless overwrite is set. The package is written beside the destination and moved " +
            "into place, so a failed export leaves whatever was there before it intact. " +
            "include_dependencies follows the whole reference graph, where one character prefab can " +
            "reach most of the project, so the reply reports the size that was actually written.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject ExportPackage(
            [McpArg("paths", "Project paths to export. A folder brings what is under it unless " +
                             "recurse is false.", Required = true)]
            string[] paths = null,
            // Not 'file': the CLI keeps that name for itself, so --file never reached this tool and
            // the documented command line failed with "'file' is required".
            [McpArg("destination", "Where to write the .unitypackage, including the file name.",
                    Required = true)]
            string file = null,
            [McpArg("recurse", "For a folder, include the assets inside it.")]
            bool recurse = true,
            [McpArg("include_dependencies", "Also include every asset the listed ones reference.")]
            bool includeDependencies = false,
            [McpArg("overwrite", "Replace the file if one is already there.")]
            bool overwrite = false)
        {
            if (paths == null || paths.Length == 0)
            {
                throw new McpToolException(
                    "invalid_params", "'paths' is required and needs at least one entry.");
            }

            var assets = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Replace('\\', '/').TrimEnd('/'))
                .ToArray();

            if (assets.Length == 0)
            {
                throw new McpToolException("invalid_params", "'paths' held no usable path.");
            }

            foreach (var asset in assets)
            {
                if (!AssetDatabase.IsValidFolder(asset) &&
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(asset) == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"No asset or folder at '{asset}'. Paths are project-relative and start at " +
                        "'Assets/' or 'Packages/'; asset_find will give you exact ones.");
                }
            }

            // The same reasoning as build_player: the file goes outside the project, and guessing
            // where is how an agent fills someone's desktop.
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'destination' is required. The package is written outside the project, so it " +
                    "has to be stated rather than assumed.");
            }

            var target = file.Replace('\\', '/');

            // A folder given without a trailing separator would otherwise take the extension onto
            // its own name and write the package beside it: 'C:/Backups' produced
            // 'C:/Backups.unitypackage', a file the caller never named and did not look for.
            if (Directory.Exists(target.TrimEnd('/')))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{file}' names a folder, not a file. Give the file name too, for example " +
                    $"'{target.TrimEnd('/')}/backup.unitypackage'.");
            }

            if (!target.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                target += ".unitypackage";
            }

            // A directory that does not exist yet reaches here as '<dir>/.unitypackage', which is
            // a hidden file with no name rather than the refusal the caller expects.
            if (Path.GetFileName(target).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{file}' names a folder, not a file. Give the file name too, for example " +
                    $"'{target.Substring(0, target.Length - ".unitypackage".Length)}backup.unitypackage'.");
            }

            if (File.Exists(target) && !overwrite)
            {
                throw new McpToolException(
                    "already_exists",
                    $"'{target}' is already there. Pass overwrite true to replace it, or choose " +
                    "another name.");
            }

            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var moved = false;
            var options = ExportPackageOptions.Default;

            if (recurse)
            {
                options |= ExportPackageOptions.Recurse;
            }

            if (includeDependencies)
            {
                options |= ExportPackageOptions.IncludeDependencies;
            }

            // ExportPackageOptions.Interactive is deliberately absent. It runs the export
            // asynchronously and opens a file browser window when it finishes, so the call would
            // return before the file exists and leave a window open on the Editor.
            //
            // Written beside the destination and moved into place afterwards. ExportPackage logs a
            // failure and returns rather than throwing, so exporting straight onto the destination
            // and then asking whether a file is there reports the previous export as this one's
            // result — and a backup tool that answers with last week's file is worse than one that
            // fails.
            // Short on purpose: the staging name sits on the destination's own path, and a long
            // suffix can push it past the limit a Windows path has without long paths enabled.
            var staging = target + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".partial";

            try
            {
                AssetDatabase.ExportPackage(assets, staging, options);

                if (!File.Exists(staging) || new FileInfo(staging).Length == 0)
                {
                    throw new McpToolException(
                        "tool_failed",
                        $"Unity wrote nothing for '{target}', and reports an export failure to the " +
                        "console rather than as an error, so console_read_logs carries the reason. " +
                        "Anything already at that path is untouched.");
                }

                if (File.Exists(target))
                {
                    if (!overwrite)
                        throw new McpToolException("already_exists", $"'{target}' appeared during export.");
                    File.Replace(staging, target, null);
                }
                else
                {
                    File.Move(staging, target);
                }

                moved = true;
            }
            catch (IOException e) when (!moved)
            {
                // The export itself succeeded and only the move did not, which is a refusal the
                // caller can act on rather than the 500 an escaping IOException becomes.
                throw new McpToolException(
                    "tool_failed",
                    $"The package was written but could not be put at '{target}': {e.Message} " +
                    "Anything already there is untouched. Nothing else was left behind, so the " +
                    "same call works once the destination is free.");
            }
            finally
            {
                // The destination folder is left as it was found, and a failure to remove the
                // staging file must not replace the error already on its way out.
                if (File.Exists(staging))
                {
                    try
                    {
                        File.Delete(staging);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            var result = new JObject
            {
                ["file"] = target,
                ["bytes"] = new FileInfo(target).Length,
                ["paths"] = new JArray(assets.Cast<object>().ToArray()),
                ["recurse"] = recurse,
                ["includeDependencies"] = includeDependencies,
            };

            if (includeDependencies)
            {
                // GetDependencies follows references, and a folder has none, so a listed folder
                // counted as a single entry however much was inside it. Its contents are expanded
                // first; with recurse off there is no way to say which of them Unity took, so the
                // count is left out rather than published wrong.
                var folders = assets.Where(AssetDatabase.IsValidFolder).ToArray();

                if (folders.Length == 0 || recurse)
                {
                    var listed = assets
                        .SelectMany(a => AssetDatabase.IsValidFolder(a) ? Inside(a) : new[] { a })
                        .Distinct()
                        .ToArray();

                    result["includedAssetCount"] = AssetDatabase.GetDependencies(listed, true)
                        .Distinct()
                        .Count();
                }
                else
                {
                    // Absent rather than wrong, and said so: a caller reading the key would
                    // otherwise find it missing with nothing to explain the difference.
                    result["note"] = "includedAssetCount is left out because a folder was listed " +
                                     "with recurse false, and which of its assets Unity took " +
                                     "cannot be worked out from here.";
                }
            }

            return result;
        }

        /// <summary>The assets a folder holds, at any depth, leaving the folders themselves out.</summary>
        private static string[] Inside(string folder)
        {
            return AssetDatabase.FindAssets(string.Empty, new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !AssetDatabase.IsValidFolder(p))
                .Distinct()
                .ToArray();
        }

        /// <summary>Overwrite an asset in place, so its GUID and the references to it survive.</summary>
        /// <remarks>
        /// DeleteAsset followed by CreateAsset writes a new file with a new GUID, and every scene,
        /// prefab and component pointing at the old one is left holding a missing reference — with
        /// nothing in the reply to say a replacement had that cost. CopySerialized carries the
        /// object's name across as well, so the asset's own name is put back afterwards.
        /// </remarks>
        internal static void ReplaceAssetContents(
            UnityEngine.Object existing, UnityEngine.Object fresh, string assetPath)
        {
            try
            {
                // The whole object is overwritten, so the whole object is what has to be on
                // the undo stack: RecordObject stores a property diff, which is not what
                // CopySerialized produces.
                Undo.RegisterCompleteObjectUndo(existing, "MCP Replace Asset");
                EditorUtility.CopySerialized(fresh, existing);
                existing.name = Path.GetFileNameWithoutExtension(assetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fresh);
            }

            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssetIfDirty(existing);
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

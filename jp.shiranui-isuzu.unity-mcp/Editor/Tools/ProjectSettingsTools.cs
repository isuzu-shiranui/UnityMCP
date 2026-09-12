using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// The settings a project carries in its own files rather than in a scene.
    /// </summary>
    /// <remarks>
    /// Every one of these is a SerializedObject over an asset under ProjectSettings/, so the same
    /// property paths the Inspector shows address them, and the same conversion reads and writes
    /// their values. Nothing here is reachable by scene path, which is why inspect_write cannot
    /// touch it.
    /// </remarks>
    internal static class ProjectSettingsTools
    {
        /// <summary>
        /// The sections, by the name a caller would use. Spelled here rather than derived from the
        /// folder: the file names are Unity's internal ones, and half of them do not say what they
        /// hold.
        /// </summary>
        private static readonly Dictionary<string, string> Sections =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["player"] = "ProjectSettings.asset",
                ["quality"] = "QualitySettings.asset",
                ["graphics"] = "GraphicsSettings.asset",
                ["tags"] = "TagManager.asset",
                ["physics"] = "DynamicsManager.asset",
                ["physics2d"] = "Physics2DSettings.asset",
                ["time"] = "TimeManager.asset",
                ["audio"] = "AudioManager.asset",
                ["input"] = "InputManager.asset",
                ["editor"] = "EditorSettings.asset",
                ["navigation"] = "NavMeshAreas.asset",
                ["presets"] = "PresetManager.asset",
                ["memory"] = "MemorySettings.asset",
                ["build"] = "EditorBuildSettings.asset",
            };

        /// <summary>Properties listed when the caller names no filter.</summary>
        private const int MaxProperties = 200;

        [McpTool(
            "project_settings",
            "Read or change a project's own settings: player, quality, graphics, tags and layers, " +
            "physics, time, audio, input, editor. These live in files under ProjectSettings/ and " +
            "have no scene path, so inspect_write cannot reach them. A change is written to disk " +
            "at once, with no save step. That write saves every dirty asset, not only this file, " +
            "so on a project with work in progress it can outlast the main-thread window and come " +
            "back as a job - and the same save can reload the domain, which takes the job's record " +
            "with it. Read the property back rather than polling job_status for it.",
            Idempotency = McpIdempotency.Unsafe,
            UndoGroup = "MCP Project Settings",
            Group = "build")]
        public static JObject ProjectSettings(
            [McpArg("section", "Which settings to read. Omit to list the sections.")]
            string section = null,
            [McpArg("property", "Serialized property path, as the Inspector shows it, e.g. " +
                                "'companyName' or 'm_Gravity'. Omit to list what the section has. " +
                                "A name that is not a whole path is matched as a substring, so " +
                                "'color' finds every property whose name contains it.")]
            string property = null,
            [McpArg("value", "The new value, shaped like the property's type as inspect_write takes " +
                             "it. Omit to read rather than change.")]
            JToken value = null,
            [McpArg("properties", "Read several of this section's properties in one call, as an " +
                                  "array of exact paths. The reply keys each by the path asked " +
                                  "for, and one that is not there carries its own error rather " +
                                  "than failing the others. Alternative to 'property'.")]
            string[] properties = null,
            [McpArg("values", "Change several of this section's properties together, as an object " +
                              "of property path to value. Written under one save, and not written " +
                              "at all if any path is missing. Turning on one layer collision pair " +
                              "took eight calls without this. Alternative to 'property' and " +
                              "'value'.")]
            JObject values = null)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                if (property != null || value != null || properties != null || values != null)
                {
                    throw new McpToolException(
                        "invalid_params",
                        "'property' and 'value' need a 'section'. Without one this lists the sections.");
                }

                return new JObject
                {
                    ["sections"] = new JArray(Sections.Keys
                        .OrderBy(k => k, StringComparer.Ordinal)
                        .Cast<object>().ToArray()),
                };
            }

            if (!Sections.TryGetValue(section, out var file))
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{section}' is not a settings section. There is: "
                    + string.Join(", ", Sections.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/" + file);

            if (assets == null || assets.Length == 0)
            {
                throw new McpToolException(
                    "not_found",
                    $"This project has no ProjectSettings/{file}. It is written the first time "
                    + "something changes that section.");
            }

            var serialized = new SerializedObject(assets[0]);

            if (values != null && values.Count > 0)
            {
                if (property != null || value != null)
                {
                    throw new McpToolException(
                        "invalid_params",
                        "'values' replaces 'property' and 'value'; pass one form or the other.");
                }

                return ChangeMany(serialized, section, values);
            }

            if (properties != null && properties.Length > 0)
            {
                if (property != null || value != null)
                {
                    throw new McpToolException(
                        "invalid_params", "'properties' replaces 'property'; pass one or the other.");
                }

                return ReadMany(serialized, section, properties);
            }

            if (value != null)
            {
                if (string.IsNullOrWhiteSpace(property))
                {
                    throw new McpToolException(
                        "invalid_params", "'value' needs the 'property' it belongs to.");
                }

                return Change(serialized, section, property, value);
            }

            return string.IsNullOrWhiteSpace(property)
                ? List(serialized, section, null)
                : Read(serialized, section, property);
        }

        /// <summary>What a write costs beyond the setting it changed.</summary>
        internal const string SavesEverythingNote =
            "SaveAssets is the only call that writes a settings file, and it writes every other " +
            "unsaved asset in the project with it. Anything left dirty on purpose is now on disk.";

        /// <summary>
        /// Persist the change. There is no way to write one settings file on its own.
        /// </summary>
        /// <remarks>
        /// SaveAssetIfDirty clears the dirty flag and leaves the file byte-for-byte unchanged:
        /// every settings singleton carries a built-in GUID, and that is what it looks its target
        /// up by. Calling it is worse than doing nothing, because the cleared flag stops the next
        /// SaveAssets from writing the file either. SaveToSerializedFileAndForget refuses an
        /// object that is already persistent, which these are. So SaveAssets it is, and the reply
        /// says what that costs rather than the tool pretending it was narrower.
        /// </remarks>
        private static void SaveSection()
        {
            AssetDatabase.SaveAssets();
        }

        /// <summary>Several of a section's properties, read in one call.</summary>
        private static JObject ReadMany(SerializedObject serialized, string section, string[] properties)
        {
            var reads = new JObject();

            foreach (var one in properties)
            {
                try
                {
                    reads[one] = Read(serialized, section, one);
                }
                catch (McpToolException e)
                {
                    reads[one] = new JObject { ["property"] = one, ["error"] = e.Message };
                }
            }

            return new JObject { ["section"] = section, ["reads"] = reads };
        }

        /// <summary>
        /// Several of a section's properties, changed together and saved once.
        /// </summary>
        /// <remarks>
        /// Nothing is applied until every path has been found, so a typo in the third of three
        /// leaves the file as it was rather than half changed. A layer collision pair is two
        /// masks and neither means anything on its own: writing one and failing the other leaves
        /// the matrix disagreeing with itself.
        /// </remarks>
        private static JObject ChangeMany(SerializedObject serialized, string section, JObject values)
        {
            var conflict = SerializedValues.BatchPathConflict(values);
            if (conflict != null)
            {
                throw new McpToolException("invalid_params", conflict);
            }

            var found = new List<(string Path, SerializedProperty Property, JToken Value)>();

            foreach (var pair in values)
            {
                var target = serialized.FindProperty(pair.Key);

                if (target == null)
                {
                    throw new McpToolException(
                        "invalid_params",
                        $"'{section}' has no property '{pair.Key}'. Nothing was written. Read the "
                        + "section without 'values' to see what it has.");
                }

                found.Add((pair.Key, target, pair.Value));
            }

            foreach (var (path, target, incoming) in found)
            {
                var problem = SerializedValues.Write(target, incoming);

                if (problem != null)
                {
                    // Update() drops every pending edit, which is how the earlier ones in this
                    // batch are undone: none of them have been applied yet.
                    serialized.Update();

                    throw new McpToolException("invalid_params", $"'{path}': {problem} Nothing was written.");
                }
            }

            serialized.ApplyModifiedProperties();
            SaveSection();
            serialized.Update();

            var written = new JObject();

            foreach (var (path, _, _) in found)
            {
                written[path] = Read(serialized, section, path)["property"];
            }

            return new JObject
            {
                ["section"] = section,
                ["written"] = written,
                ["note"] = SavesEverythingNote,
            };
        }

        private static JObject Change(
            SerializedObject serialized, string section, string property, JToken value)
        {
            var found = serialized.FindProperty(property);

            if (found == null)
            {
                throw new McpToolException(
                    "invalid_params",
                    $"'{section}' has no property '{property}'. Read the section without 'value' "
                    + "to see what it has; the serialized name often differs from the Inspector's "
                    + "label.");
            }

            var problem = SerializedValues.Write(found, value);

            if (problem != null)
            {
                throw new McpToolException("invalid_params", problem);
            }

            serialized.ApplyModifiedProperties();
            SaveSection();
            serialized.Update();

            return new JObject
            {
                ["section"] = section,
                ["property"] = SerializedValues.Describe(serialized.FindProperty(property)),
                ["written"] = true,
                ["note"] = SavesEverythingNote,
            };
        }

        private static JObject Read(SerializedObject serialized, string section, string property)
        {
            var found = serialized.FindProperty(property);

            // A path that is not one falls back to a search, because the Inspector's label and the
            // serialized name differ often enough that a caller cannot be expected to know it.
            return found == null
                ? List(serialized, section, property)
                : new JObject
                {
                    ["section"] = section,
                    ["property"] = SerializedValues.Describe(found),
                };
        }

        private static JObject List(SerializedObject serialized, string section, string filter)
        {
            var iterator = serialized.GetIterator();
            var properties = new JArray();
            var total = 0;
            var searching = !string.IsNullOrEmpty(filter);

            // A search descends; a listing does not. Half the names worth finding — a quality
            // level's shadow distance, an input axis — exist only inside an array or a struct,
            // and stopping at the top level reports them as absent.
            var enterChildren = true;

            // Next rather than NextVisible, for the reason SerializedValues.TopLevel gives: a
            // settings asset is drawn by its own Inspector, and the physics layer collision
            // matrix — where "why does this not collide" ends — is invisible to the other walk.
            while (iterator.Next(enterChildren))
            {
                enterChildren = searching;

                if (SerializedValues.IsBookkeeping(iterator.propertyPath))
                {
                    continue;
                }

                total++;

                if (searching
                    && iterator.propertyPath.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                properties.Add(SerializedValues.Describe(iterator));

                // One past the cap, so a section holding exactly the cap is not reported as cut
                // short. The extra entry is dropped below.
                if (properties.Count > MaxProperties)
                {
                    break;
                }
            }

            var cut = properties.Count > MaxProperties;

            if (cut)
            {
                properties.RemoveAt(properties.Count - 1);
            }

            var listed = new JObject
            {
                ["section"] = section,
                ["properties"] = properties,
            };

            // Only a walk that ran to the end knows the total, and only then is it worth saying.
            if (!cut)
            {
                listed["propertyCount"] = total;
            }



            if (cut)
            {
                listed["truncated"] = $"stopped at {MaxProperties}; narrow it with 'property'";
            }

            return listed;
        }
    }
}

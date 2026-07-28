using System;
using System.IO;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEditor;
using UnityEditor.Build.Reporting;

using UnityEngine;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Player builds, and the settings they run from.
    /// </summary>
    /// <remarks>
    /// Building belongs in the Editor rather than behind the Hub CLI: it needs the open project's
    /// scenes, its build settings and its active target, none of which the Hub knows about.
    /// Installing editors and modules is the opposite case and is left to the Hub — see the
    /// message on <see cref="SwitchTarget"/> when a target is not installed.
    /// <para>
    /// A build takes minutes, so these calls cross the synchronous window and come back as a job
    /// id. That is the intended path: poll it rather than calling again.
    /// </para>
    /// </remarks>
    internal static class BuildTools
    {
        [McpTool(
            "build_settings",
            "Report the active build target, the scenes in the build, and whether the required " +
            "module is installed. Read this before building — the scene list is the most common " +
            "reason a build produces something unexpected.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Settings()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;

            var scenes = new JArray(EditorBuildSettings.scenes.Select((s, i) => (object)new JObject
            {
                ["path"] = s.path,
                ["enabled"] = s.enabled,
                ["buildIndex"] = i,
            }).ToArray());

            return new JObject
            {
                ["activeBuildTarget"] = active.ToString(),
                ["activeBuildTargetGroup"] = BuildPipeline.GetBuildTargetGroup(active).ToString(),
                ["supported"] = BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(active), active),
                ["developmentBuild"] = EditorUserBuildSettings.development,
                ["scriptDebugging"] = EditorUserBuildSettings.allowDebugging,
                ["sceneCount"] = EditorBuildSettings.scenes.Length,
                ["enabledSceneCount"] = EditorBuildSettings.scenes.Count(s => s.enabled),
                ["scenes"] = scenes,
                ["productName"] = PlayerSettings.productName,
                ["companyName"] = PlayerSettings.companyName,
                ["bundleVersion"] = PlayerSettings.bundleVersion,
            };
        }

        [McpTool(
            "build_player",
            "Build a player. Takes minutes, so it returns a job id to poll rather than an answer. " +
            "The scenes in the build settings are used unless a list is given.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject BuildPlayer(
            [McpArg("output_path", "Where to write the build, including the executable's file name.")]
            string outputPath = null,
            [McpArg("platform", "Build target, e.g. StandaloneWindows64, Android, iOS, WebGL. Defaults to the active one.")]
            string platform = null,
            [McpArg("scenes", "Scene paths to include; omit to use the enabled scenes in the build settings.")]
            string[] scenes = null,
            [McpArg("development", "Make a development build.")]
            bool development = false)
        {
            // No default output path. A build writes outside the project, and guessing where is
            // how an agent fills someone's desktop or overwrites a shipped folder.
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new McpToolException(
                    "invalid_params",
                    "'output_path' is required. Builds are written outside the project, so the " +
                    "destination has to be stated rather than assumed.");
            }

            var buildTarget = ResolveTarget(platform);
            var group = BuildPipeline.GetBuildTargetGroup(buildTarget);

            if (!BuildPipeline.IsBuildTargetSupported(group, buildTarget))
            {
                throw new McpToolException(
                    "unsupported_platform",
                    $"The {buildTarget} module is not installed for this Editor. Install it with the " +
                    "Hub CLI: \"Unity Hub.exe\" -- --headless install-modules --version <editor> " +
                    "--module <module>.",
                    501);
            }

            var sceneList = scenes != null && scenes.Length > 0
                ? scenes
                : EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();

            if (sceneList.Length == 0)
            {
                throw new McpToolException(
                    "invalid_params",
                    "No scenes to build. Enable some in the build settings, or pass 'scenes'. " +
                    "Building with none produces an empty player rather than an error.");
            }

            foreach (var scene in sceneList)
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scene) == null)
                {
                    throw new McpToolException("not_found", $"No scene at '{scene}'.");
                }
            }

            var directory = Path.GetDirectoryName(outputPath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new BuildPlayerOptions
            {
                scenes = sceneList,
                locationPathName = outputPath,
                target = buildTarget,
                targetGroup = group,
                options = development
                    ? BuildOptions.Development | BuildOptions.AllowDebugging
                    : BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;

            var messages = new JArray(report.steps
                .SelectMany(step => step.messages)
                .Where(m => m.type == LogType.Error || m.type == LogType.Exception)
                .Take(20)
                .Select(m => (object)new JObject { ["type"] = m.type.ToString(), ["message"] = m.content })
                .ToArray());

            var result = new JObject
            {
                ["result"] = summary.result.ToString(),
                ["succeeded"] = summary.result == BuildResult.Succeeded,
                ["outputPath"] = summary.outputPath,
                ["target"] = summary.platform.ToString(),
                ["totalSizeBytes"] = summary.totalSize,
                ["totalSeconds"] = Math.Round(summary.totalTime.TotalSeconds, 1),
                ["errors"] = summary.totalErrors,
                ["warnings"] = summary.totalWarnings,
                ["sceneCount"] = sceneList.Length,
                ["messages"] = messages,
            };

            if (summary.result != BuildResult.Succeeded)
            {
                // Reported as a value rather than thrown: a failed build is an outcome with a
                // report worth reading, not a malformed request.
                result["note"] = "The build did not succeed. console_read_logs has the full output.";
            }

            return result;
        }

        [McpTool(
            "build_switch_target",
            "Switch the active build target. Reimports assets for the new platform, so it takes a " +
            "while and comes back as a job.",
            Idempotency = McpIdempotency.Unsafe)]
        public static JObject SwitchTarget(
            [McpArg("platform", "Build target to switch to, e.g. StandaloneWindows64, Android, iOS, WebGL.")]
            string platform = null)
        {
            var buildTarget = ResolveTarget(platform);
            var group = BuildPipeline.GetBuildTargetGroup(buildTarget);

            if (EditorUserBuildSettings.activeBuildTarget == buildTarget)
            {
                return new JObject
                {
                    ["switched"] = false,
                    ["activeBuildTarget"] = buildTarget.ToString(),
                    ["note"] = "Already the active target.",
                };
            }

            if (!BuildPipeline.IsBuildTargetSupported(group, buildTarget))
            {
                throw new McpToolException(
                    "unsupported_platform",
                    $"The {buildTarget} module is not installed for this Editor. Install it with the " +
                    "Hub CLI: \"Unity Hub.exe\" -- --headless install-modules --version <editor> " +
                    "--module <module>.",
                    501);
            }

            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, buildTarget))
            {
                throw new McpToolException("tool_failed", $"Unity would not switch to {buildTarget}.");
            }

            return new JObject
            {
                ["switched"] = true,
                ["activeBuildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
            };
        }

        // Named "platform" on the wire: "target" is reserved by the invoker for choosing which
        // Editor a call goes to, and the catalog refuses a tool that shadows it.
        private static BuildTarget ResolveTarget(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                return EditorUserBuildSettings.activeBuildTarget;
            }

            if (Enum.TryParse<BuildTarget>(platform, true, out var parsed) && parsed != BuildTarget.NoTarget)
            {
                return parsed;
            }

            var known = string.Join(", ", new[]
            {
                BuildTarget.StandaloneWindows64, BuildTarget.StandaloneOSX, BuildTarget.StandaloneLinux64,
                BuildTarget.Android, BuildTarget.iOS, BuildTarget.WebGL,
            }.Select(t => t.ToString()));

            throw new McpToolException(
                "invalid_params",
                $"'{platform}' is not a build target. Common ones: {known}.");
        }
    }
}

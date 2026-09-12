using System;
using System.Collections.Generic;
using System.IO;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Finds the <c>isuzu-unity-cli</c> executable the way a shell would, plus the directories winget
    /// and the install script use, which a GUI-launched Editor may not have on its PATH.
    /// </summary>
    internal static class IsuzuCliLocator
    {
        public const string ExecutableName = "isuzu-unity-cli";

        /// <summary>The folder winget unpacks the package into when it comes from winget's own source.</summary>
        internal const string WingetPackageFolder = "IsuzuShiranui.IsuzuUnityCli_Microsoft.Winget.Source_8wekyb3d8bbwe";

        public const string InstallScriptUrlWindows = "https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1";
        public const string InstallScriptUrlUnix = "https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh";

        public static bool TryFind(out string path)
        {
            var isWindows = Path.DirectorySeparatorChar == '\\';

            foreach (var candidate in Candidates(Environment.GetEnvironmentVariable, isWindows))
            {
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }

            path = null;
            return false;
        }

        /// <summary>Where the executable may be, in the order the first match is taken.</summary>
        /// <remarks>
        /// winget links the command into its folder of links when it can create a symbolic link, and
        /// otherwise puts the package folder itself on PATH. Either folder reaches PATH with the
        /// install, and an Editor started before that keeps the PATH it started with.
        /// </remarks>
        internal static List<string> Candidates(Func<string, string> environment, bool isWindows)
        {
            var fileName = isWindows ? ExecutableName + ".exe" : ExecutableName;
            var candidates = new List<string>();

            foreach (var entry in (environment("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                // Windows accepts an entry in double quotes. On Unix the quotes and the spaces around
                // an entry are part of the directory's name.
                var directory = isWindows ? entry.Trim().Trim('"') : entry;

                if (directory.Length == 0)
                {
                    continue;
                }

                try
                {
                    candidates.Add(Path.Combine(directory, fileName));
                }
                catch (ArgumentException)
                {
                    // Not a path, and a shell skips it too.
                }
            }

            if (isWindows)
            {
                var local = environment("LOCALAPPDATA");
                var programFiles = environment("ProgramFiles");

                AddUnder(candidates, local, fileName, "Microsoft", "WinGet", "Links");
                AddUnder(candidates, programFiles, fileName, "WinGet", "Links");
                AddUnder(candidates, local, fileName, "Microsoft", "WinGet", "Packages", WingetPackageFolder);
                AddUnder(candidates, programFiles, fileName, "WinGet", "Packages", WingetPackageFolder);
                AddUnder(candidates, local, fileName, "Programs", ExecutableName);
            }
            else
            {
                AddUnder(candidates, environment("HOME"), fileName, ".local", "bin");
            }

            return candidates;
        }

        /// <remarks>
        /// A variable that is not set adds nothing. Combined anyway, it makes a relative path, which
        /// File.Exists resolves against the Editor's working directory: the project.
        /// </remarks>
        private static void AddUnder(List<string> candidates, string root, string fileName, params string[] folders)
        {
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            candidates.Add(Path.Combine(root, Path.Combine(folders), fileName));
        }

        /// <summary>The shell command that installs the CLI, shown to the user and run by the Settings window.</summary>
        public static string InstallCommand()
        {
            return Path.DirectorySeparatorChar == '\\'
                ? $"irm {InstallScriptUrlWindows} | iex"
                : $"curl -fsSL {InstallScriptUrlUnix} | sh";
        }
    }
}

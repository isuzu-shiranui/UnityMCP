using System;
using System.IO;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Finds the <c>isuzu-unity-cli</c> executable the way a shell would, plus the directory the
    /// install script uses, which a GUI-launched Editor may not have on its PATH.
    /// </summary>
    internal static class IsuzuCliLocator
    {
        public const string ExecutableName = "isuzu-unity-cli";

        public const string InstallScriptUrlWindows = "https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.ps1";
        public const string InstallScriptUrlUnix = "https://raw.githubusercontent.com/isuzu-shiranui/UnityMCP/main/install.sh";

        public static bool TryFind(out string path)
        {
            var isWindows = Path.DirectorySeparatorChar == '\\';
            var fileName = isWindows ? ExecutableName + ".exe" : ExecutableName;

            var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
            foreach (var directory in candidates)
            {
                if (directory.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }

            var home = Environment.GetEnvironmentVariable(isWindows ? "USERPROFILE" : "HOME") ?? string.Empty;
            var installed = isWindows
                ? Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? string.Empty, "Programs", ExecutableName, fileName)
                : Path.Combine(home, ".local", "bin", fileName);

            if (installed.Length > fileName.Length && File.Exists(installed))
            {
                path = installed;
                return true;
            }

            path = null;
            return false;
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

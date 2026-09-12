using System;
using System.Globalization;
using System.IO;

using UnityMCP.Editor.Core;

namespace UnityMCP.Editor.Settings
{
    /// <summary>
    /// Whether a newer release exists, according to what the CLI last found out.
    /// </summary>
    /// <remarks>
    /// This package makes no outbound request, and adding the first one to ask GitHub on a timer
    /// would change what installing it means. It does not have to: the CLI already asks, and
    /// already writes the answer beside the descriptors this Editor writes, under the same state
    /// root. Reading that file costs nothing and keeps the property.
    /// <para>
    /// The consequence is that the answer only exists on a machine where the CLI has run. With no
    /// file there is nothing to say, which is the same thing this page does when there is no
    /// newer release.
    /// </para>
    /// </remarks>
    internal static class ReleaseNotice
    {
        /// <summary>Written by the CLI's release check, read here.</summary>
        private static string CachePath =>
            Path.Combine(McpInstanceDescriptor.StateRoot, "cache", "latest-release.json");

        /// <summary>
        /// The newest tag the CLI knows about, or null when it knows of none or has never looked.
        /// </summary>
        public static string Tag()
        {
            try
            {
                var path = CachePath;

                if (!File.Exists(path))
                {
                    return null;
                }

                // Two lines, written by hand on the other side: the tag, then when it was fetched.
                // The time is not read here - a stale entry cannot name a release that does not
                // exist, and going quiet about one that does helps nobody.
                var lines = File.ReadAllLines(path);

                return lines.Length > 0 && lines[0].Length > 0 ? lines[0] : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Whether <paramref name="tag"/> names a release newer than <paramref name="current"/>.</summary>
        /// <remarks>
        /// Field by field as numbers. Compared as text, 4.10.0 sorts before 4.9.0, which is how a
        /// newer release ends up reported as an older one.
        /// </remarks>
        public static bool IsNewer(string tag, string current)
        {
            var released = Parts(tag);
            var running = Parts(current);

            if (released.Length == 0 || running.Length == 0)
            {
                return false;
            }

            for (var i = 0; i < Math.Max(released.Length, running.Length); i++)
            {
                var a = i < released.Length ? released[i] : 0;
                var b = i < running.Length ? running[i] : 0;

                if (a != b)
                {
                    return a > b;
                }
            }

            return false;
        }

        private static int[] Parts(string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                return Array.Empty<int>();
            }

            var pieces = version.TrimStart('v', 'V').Split('.', '-', '+');
            var numbers = new System.Collections.Generic.List<int>();

            foreach (var piece in pieces)
            {
                if (!int.TryParse(piece, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    break;
                }

                numbers.Add(value);
            }

            return numbers.ToArray();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Rebuilds the tool catalog when a definition file or a script it names changes in one of
    /// the definition directories.
    /// </summary>
    /// <remarks>
    /// Editors save through a temporary file and a rename, and a save produces several events,
    /// so the rebuild runs once after the burst goes quiet. Only the definition directories are
    /// watched: a script named by an absolute path elsewhere is read on every call anyway.
    /// </remarks>
    internal sealed class DefinedToolsWatcher : IDisposable
    {
        private const int DebounceMs = 300;

        private readonly List<FileSystemWatcher> watchers = new();
        private readonly Timer timer;
        private readonly Action refresh;
        private readonly Action<string> log;
        private int refreshFailed;

        // Read from timer and watcher threads, written by Dispose on the main thread.
        private volatile bool disposed;

        public DefinedToolsWatcher(IEnumerable<string> directories, Action refresh, Action<string> log)
        {
            this.refresh = refresh;
            this.log = log;
            this.timer = new Timer(_ => this.Fire(), null, Timeout.Infinite, Timeout.Infinite);

            foreach (var directory in directories)
            {
                try
                {
                    Directory.CreateDirectory(directory);

                    var watcher = new FileSystemWatcher(directory)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        IncludeSubdirectories = false,
                    };

                    watcher.Changed += this.OnChanged;
                    watcher.Created += this.OnChanged;
                    watcher.Deleted += this.OnChanged;
                    watcher.Renamed += this.OnRenamed;
                    watcher.EnableRaisingEvents = true;
                    this.watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    log?.Invoke(
                        $"[DefinedTools] Not watching {directory}: {ex.Message}. " +
                        "After editing a definition, call GET /tools?refresh=1 to reload.");
                }
            }
        }

        /// <summary>How many directories are being watched.</summary>
        public int WatchedDirectoryCount => this.watchers.Count;

        private static bool IsDefinitionFile(string path)
        {
            var extension = Path.GetExtension(path);

            return string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (IsDefinitionFile(e.FullPath))
            {
                this.Schedule();
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (IsDefinitionFile(e.FullPath) || IsDefinitionFile(e.OldFullPath))
            {
                this.Schedule();
            }
        }

        private void Schedule()
        {
            if (this.disposed)
            {
                return;
            }

            try
            {
                this.timer.Change(DebounceMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Disposed between the check and the call; nothing left to schedule.
            }
        }

        private void Fire()
        {
            // The debounce may have expired while the server was stopping; a refresh now would
            // rebuild a catalog nothing will serve.
            if (this.disposed)
            {
                return;
            }

            try
            {
                this.refresh();
                Interlocked.Exchange(ref this.refreshFailed, 0);
            }
            catch (Exception ex)
            {
                // One line per failure streak: a directory being written continuously would
                // otherwise fill the console with the same message.
                if (Interlocked.Exchange(ref this.refreshFailed, 1) == 0)
                {
                    this.log?.Invoke(
                        $"[DefinedTools] Reloading definitions failed: {ex.Message}. " +
                        "Call GET /tools?refresh=1 to reload them.");
                }
            }
        }

        public void Dispose()
        {
            this.disposed = true;
            this.timer.Dispose();

            foreach (var watcher in this.watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch (Exception)
                {
                    // A watcher on a directory that has since vanished cannot be stopped more than this.
                }
            }

            this.watchers.Clear();
        }
    }
}

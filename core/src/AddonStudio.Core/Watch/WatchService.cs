using System.Collections.Concurrent;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Util;

namespace AddonStudio.Core.Watch;

/// <summary>
/// SPEC 6.9: one FileSystemWatcher per watched project on the repo folder, keeping only changes under content/ and
/// prebuilt/ (so either folder can be created or replaced later), ignoring .git/ and .addonstudio/, debounced 750 ms.
/// </summary>
public sealed class WatchService : IWatchService
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(750);

    private static readonly string[] IgnoredSegments = [".git", StatePaths.DirName];

    private readonly IProjectService projects;
    private readonly ConcurrentDictionary<string, ProjectWatch> watches = new(Paths.FsComparer);
    private readonly object gate = new();
    private bool disposed;

    public WatchService(IProjectService projects)
    {
        this.projects = projects;
    }

    /// <summary>The quiet time before changes are reported, settable for tests.</summary>
    public TimeSpan Debounce { get; init; } = DefaultDebounce;

    public event Action<WatchChangedEvent>? Changed;

    /// <summary>The projects being watched now (full paths).</summary>
    public IReadOnlyCollection<string> Watched => watches.Keys.ToList();

    public Task<WatchSetResult> SetAsync(string path, bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (!enabled)
            {
                if (watches.TryRemove(Paths.Full(path), out var existing))
                {
                    existing.Dispose();
                }

                return Task.FromResult(new WatchSetResult(false));
            }

            var paths = projects.GetPaths(Paths.Full(path));
            var key = Paths.Full(paths.Repo);

            if (!watches.ContainsKey(key))
            {
                var folders = new[] { paths.Content, paths.Prebuilt }
                    .Where(folder => Paths.IsUnder(key, folder))
                    .Select(folder => Paths.Relative(key, folder))
                    .ToArray();

                watches[key] = new ProjectWatch(paths.Repo, key, folders, Debounce, Raise);
            }

            return Task.FromResult(new WatchSetResult(true));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            foreach (var key in watches.Keys.ToList())
            {
                if (watches.TryRemove(key, out var watch))
                {
                    watch.Dispose();
                }
            }
        }
    }

    private void Raise(WatchChangedEvent change)
    {
        try
        {
            Changed?.Invoke(change);
        }
        catch (Exception exception)
        {
            Log.Warn($"A watch.changed handler failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The repo-relative forward-slash path when it is under one of the folders and not in .git/ or .addonstudio/,
    /// else null. Takes the event's Name (relative to the watched folder), not FullPath, so a temp folder reached
    /// through a symlink (macOS /var -> /private/var) still matches.
    /// </summary>
    internal static string? Filter(IReadOnlyList<string> folders, string? name)
    {
        if (string.IsNullOrEmpty(name) || !Paths.TryNormalizeRelative(name, out var relative))
        {
            return null;
        }

        var segments = relative.Split('/');

        if (segments.Any(segment => IgnoredSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        foreach (var folder in folders)
        {
            if (relative.Equals(folder, Paths.RelComparison) || relative.StartsWith(folder + "/", Paths.RelComparison))
            {
                return relative;
            }
        }

        return null;
    }

    private sealed class ProjectWatch : IDisposable
    {
        private readonly string repo;
        private readonly string[] folders;
        private readonly TimeSpan debounce;
        private readonly Action<WatchChangedEvent> raise;
        private readonly FileSystemWatcher watcher;
        private readonly Timer timer;
        private readonly HashSet<string> pending = new(Paths.RelComparer);
        private readonly object gate = new();
        private bool disposed;

        public ProjectWatch(string repo, string root, string[] folders, TimeSpan debounce, Action<WatchChangedEvent> raise)
        {
            this.repo = repo;
            this.folders = folders;
            this.debounce = debounce;
            this.raise = raise;

            timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };

            watcher.Changed += (_, e) => Add(e.Name);
            watcher.Created += (_, e) => Add(e.Name);
            watcher.Deleted += (_, e) => Add(e.Name);
            watcher.Renamed += (_, e) =>
            {
                Add(e.OldName);
                Add(e.Name);
            };
            // a buffer overflow loses events: report the watched folders themselves so the UI rebuilds
            watcher.Error += (_, _) =>
            {
                foreach (var folder in folders)
                {
                    Add(folder);
                }
            };

            watcher.EnableRaisingEvents = true;
        }

        private void Add(string? name)
        {
            if (Filter(folders, name) is not { } relative)
            {
                return;
            }

            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                pending.Add(relative);
                timer.Change(debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void Flush()
        {
            List<string> files;

            lock (gate)
            {
                if (disposed || pending.Count == 0)
                {
                    return;
                }

                files = pending.Order(StringComparer.OrdinalIgnoreCase).ToList();
                pending.Clear();
            }

            raise(new WatchChangedEvent(repo, files));
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
            }

            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            timer.Dispose();
        }
    }
}

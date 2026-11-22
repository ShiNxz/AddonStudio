namespace AddonStudio.Core.Contracts;

/// <summary>
/// FileSystemWatcher on &lt;repo&gt;/content and &lt;repo&gt;/prebuilt, debounced 750 ms, ignoring .addonstudio/ and .git/ (SPEC 6.9).
/// Changes are raised through Changed; the host forwards them as the global watch.changed event.
/// </summary>
public interface IWatchService : IDisposable
{
    /// <summary>Raised (on a thread pool thread) after the debounce with the repo and its changed repo-relative paths.</summary>
    event Action<WatchChangedEvent>? Changed;

    /// <summary>watch.set: starts or stops watching the project.</summary>
    Task<WatchSetResult> SetAsync(string path, bool enabled, CancellationToken ct = default);
}

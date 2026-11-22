namespace AddonStudio.Core.Contracts;

/// <summary>
/// App settings and the recent projects list (SPEC 6.2). File: %APPDATA%/AddonStudio/settings.json on Windows,
/// ~/.config/AddonStudio/settings.json elsewhere, or $ADDONSTUDIO_HOME/settings.json when that env var is set.
/// Synchronous on purpose: a small cached file other services read on hot paths. Thread-safe.
/// </summary>
public interface ISettingsStore
{
    /// <summary>The folder settings.json lives in (also the home of app-wide state).</summary>
    string HomeDir { get; }

    /// <summary>Full path of settings.json.</summary>
    string SettingsPath { get; }

    /// <summary>Current settings, recentProjects newest first with exists worked out now.</summary>
    AppSettings Get();

    /// <summary>settings.set: applies the patch, saves, returns the new settings.</summary>
    AppSettings Set(AppSettingsPatch patch);

    /// <summary>Adds or moves a project to the top of the recent list (max AppSettings.MaxRecentProjects), saves, returns the list.</summary>
    IReadOnlyList<RecentProject> TouchRecent(string path, string addon, string title);

    /// <summary>Removes a project from the recent list (never deletes files), saves, returns the list.</summary>
    IReadOnlyList<RecentProject> RemoveRecent(string path);
}

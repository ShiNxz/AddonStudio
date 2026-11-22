namespace AddonStudio.Core.Contracts;

/// <summary>What this machine has: OS, Steam, CS2, the Workshop Tools compiler, git, git-lfs, gh (SPEC 6.1).</summary>
public interface IEnvironmentService
{
    /// <summary>The OS the core runs on.</summary>
    Os Os { get; }

    /// <summary>env.doctor: every check (os, steam, cs2, workshopTools, steamRunning, git, gitLfs, gh) with a fix text.</summary>
    Task<DoctorReport> DoctorAsync(CancellationToken ct = default);

    /// <summary>CS2 install folder (holds game/ and content/): settings.cs2PathOverride, else WorkshopManager.FromSteamInstall().GamePath. Null when not found.</summary>
    string? Cs2Path();

    /// <summary>&lt;cs2&gt;/game/bin/win64/resourcecompiler.exe when it exists, else null.</summary>
    string? CompilerPath();

    /// <summary>&lt;cs2&gt;/game/csgo/gameinfo.gi when it exists, else null.</summary>
    string? GameInfoPath();

    /// <summary>Cs2Path() or throws cs2_missing.</summary>
    string RequireCs2Path();

    /// <summary>CompilerPath() or throws not_windows (off Windows), cs2_missing or tools_missing.</summary>
    string RequireCompilerPath();

    /// <summary>Windows and the compiler exists.</summary>
    bool CanCompile();

    /// <summary>Windows and CS2 is found (the Steam client is checked when publishing).</summary>
    bool CanPublish();

    /// <summary>Forget cached lookups (after settings change).</summary>
    void Invalidate();
}

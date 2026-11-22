namespace AddonStudio.Core.Contracts;

/// <summary>What goes into the VPK and exporting it (SPEC 5 and 6.6).</summary>
public interface IPackService
{
    /// <summary>pack.contents: the files the project's pack mode takes, diffed against last-publish.json, plus what Valve's whitelist would drop.</summary>
    Task<PackContents> ContentsAsync(string path, CancellationToken ct = default);

    /// <summary>pack.export: AddonPackager.Stage into outDir (default .addonstudio/export/&lt;timestamp&gt;).</summary>
    Task<PackExportResult> ExportAsync(string path, string? outDir, CancellationToken ct = default);

    /// <summary>
    /// The project's workshop.json pack mode and rules, the one file selection pack, export and publish use. The rules are
    /// CS2WorkshopManager AddonRules, which match path prefixes, not globs, so globs are expanded against the build dir's files.
    /// </summary>
    Pack.PackSelection Selection(string path);
}

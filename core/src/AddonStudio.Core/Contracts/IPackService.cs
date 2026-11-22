namespace AddonStudio.Core.Contracts;

/// <summary>What goes into the VPK and exporting it (SPEC 5 and 6.6).</summary>
public interface IPackService
{
    /// <summary>pack.contents: the files AddonPackager.CollectFiles would pack, diffed against last-publish.json.</summary>
    Task<PackContents> ContentsAsync(string path, CancellationToken ct = default);

    /// <summary>pack.export: AddonPackager.Stage into outDir (default .addonstudio/export/&lt;timestamp&gt;).</summary>
    Task<PackExportResult> ExportAsync(string path, string? outDir, CancellationToken ct = default);

    /// <summary>
    /// The project's workshop.json pack globs as CS2WorkshopManager rules, to hand to CollectFiles and AddonVpk (pack, export, publish).
    /// AddonRules match path prefixes, not globs, so globs are expanded against the build dir's files.
    /// </summary>
    CS2WorkshopManager.AddonRules BuildRules(string path);
}

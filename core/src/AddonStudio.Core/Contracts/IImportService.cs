namespace AddonStudio.Core.Contracts;

/// <summary>Turns an existing CS2 addon into a project (SPEC 6.4).</summary>
public interface IImportService
{
    /// <summary>import.plan: what would be copied, decompiled, kept as prebuilt or skipped.</summary>
    Task<ImportPlan> PlanAsync(string addon, string targetPath, bool decompile, CancellationToken ct = default);

    /// <summary>import.run: copies (never moves), creates the project, backs up the old content folder and links. import.progress events.</summary>
    Task<ImportResult> RunAsync(string addon, string targetPath, bool decompile, IEventSink events, CancellationToken ct = default);
}

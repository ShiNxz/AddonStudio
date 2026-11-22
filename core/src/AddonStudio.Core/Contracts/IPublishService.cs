namespace AddonStudio.Core.Contracts;

/// <summary>Build, upload through the Steam client, write last-publish.json, tag, push, release (SPEC 5).</summary>
public interface IPublishService
{
    /// <summary>publish.run: publish.progress events. SourceFolderConflictException becomes error "conflict".</summary>
    Task<PublishResult> RunAsync(PublishRequest request, IEventSink events, CancellationToken ct = default);
}

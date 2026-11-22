using System.Globalization;
using AddonStudio.Core.Contracts;
using AddonStudio.Core.Pack;
using AddonStudio.Core.Steam;
using CS2WorkshopManager;
using StbImageSharp;
using StbImageWriteSharp;
using Steamworks;

namespace AddonStudio.Core.Publish;

/// <summary>What the upload step hands to Steam (SPEC 5 publish flow, step 2).</summary>
/// <param name="Cs2Path">CS2 install folder, the build dir is &lt;cs2&gt;/game/csgo_addons/&lt;AddonName&gt;.</param>
/// <param name="PublishedFileId">Null creates a new item.</param>
/// <param name="Description">Null leaves Steam's description as it is.</param>
/// <param name="PreviewPath">Full path of the thumbnail, null leaves the published one.</param>
/// <param name="Selection">What goes into the VPK (the project's pack mode and workshop.json rules).</param>
internal sealed record UploadRequest(
    string Cs2Path,
    string AddonName,
    ulong? PublishedFileId,
    string Title,
    string? Description,
    Visibility Visibility,
    IReadOnlyList<string> Tags,
    string? PreviewPath,
    PackSelection Selection,
    string ChangeNote,
    bool AllowSourceFolderChange);

internal sealed record UploadResult(ulong PublishedFileId, bool NeedsAgreement);

/// <summary>The Steam upload behind a seam, so the publish flow runs in tests with a fake.</summary>
internal interface IWorkshopUploader
{
    /// <summary>Throws when an upload can not work here (not_windows, steam_unavailable), before anything is built.</summary>
    void EnsureReady();

    /// <summary>
    /// Packs and uploads. Exceptions of CS2WorkshopManager and Steamworks come out as they are (the publish service maps
    /// them). The upload itself can not be cancelled once it started: Steam keeps the item consistent only when it finishes.
    /// </summary>
    Task<UploadResult> UploadAsync(UploadRequest request, IProgress<float> progress, CancellationToken ct);
}

/// <summary>
/// The real upload (SPEC 5 "our own uploader"): mirrors WorkshopManager.PublishAsync step for step (source folder check,
/// CreateItem when new, StartItemUpdate, title, description, visibility, preview, SetItemContent of our staging folder,
/// tags, SubmitItemUpdate with GetItemUpdateProgress) over our own SteamConnection, and stages with AddonVpk so the
/// project's PackSelection picks the files and the maxPackBytes setting is the cap instead of the library's 3 GiB constant.
/// </summary>
internal sealed class SteamWorkshopUploader(Func<long> limitBytes) : IWorkshopUploader
{
    public SteamWorkshopUploader()
        : this(() => AddonPackager.MaxTotalSize)
    {
    }

    public void EnsureReady()
    {
        SteamGate.RequireSteam("Publishing to the Steam Workshop", notWindowsError: true);
    }

    public async Task<UploadResult> UploadAsync(UploadRequest request, IProgress<float> progress, CancellationToken ct)
    {
        EnsureReady();

        await SteamGate.Lock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // staging packs synchronously, keep that off the caller's thread
            return await Task.Run(() => UploadCoreAsync(request, progress, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Steamworks.SteamUnavailableException or IOException)
        {
            // a dead pipe stays dead, the next call opens a new one
            SteamConnection.Reset();
            throw;
        }
        finally
        {
            SteamGate.Lock.Release();
        }
    }

    private async Task<UploadResult> UploadCoreAsync(UploadRequest request, IProgress<float> progress, CancellationToken ct)
    {
        var addonsRoot = Path.Combine(request.Cs2Path, "game", "csgo_addons");
        var addonPath = Path.Combine(addonsRoot, request.AddonName);
        var limit = limitBytes();

        // what can fail without Steam is checked before a new item is created, so a mistake leaves no empty item behind
        if (!Directory.Exists(addonPath))
        {
            throw new DirectoryNotFoundException($"Addon folder '{addonPath}' does not exist.");
        }

        if (request.PreviewPath != null)
        {
            WorkshopManager.ValidateThumbnailImage(request.PreviewPath);
        }

        var totalSize = request.Selection.Collect(addonPath).Sum(file => file.Length);

        if (totalSize >= limit)
        {
            throw new AddonStudioException(ErrorCodes.InvalidParams,
                $"The pack is {AddonContents.FormatSize(totalSize)}, over the size cap of {AddonContents.FormatSize(limit)} (app settings).",
                new { limitBytes = limit, totalSize });
        }

        var client = SteamConnection.Get();
        var ugc = client.UGC;

        if (request.PublishedFileId is ulong existing && !request.AllowSourceFolderChange)
        {
            var installFolder = ugc.GetItemInstallFolder(existing);
            var previous = installFolder == null ? null : AddonPackager.ReadPublishedSourceFolder(installFolder);

            if (previous != null && !previous.Equals(request.AddonName, StringComparison.OrdinalIgnoreCase))
            {
                throw new SourceFolderConflictException(existing, previous, request.AddonName);
            }
        }

        ct.ThrowIfCancellationRequested();

        var creating = request.PublishedFileId == null;
        var needsAgreement = false;
        ulong publishedFileId;

        if (request.PublishedFileId is ulong id)
        {
            publishedFileId = id;
        }
        else
        {
            var createdItem = await client.WaitForCallResultAsync<CreateItemResult>(ugc.CreateItem(SteamConnection.AppId, EWorkshopFileType.Community)).ConfigureAwait(false);

            if (createdItem.Result != EResult.OK)
            {
                throw new InvalidOperationException($"CreateItem failed: {createdItem.Result}");
            }

            publishedFileId = createdItem.PublishedFileId;
            needsAgreement = createdItem.UserNeedsToAcceptWorkshopLegalAgreement;
        }

        // from here on the item exists, so the upload runs to the end
        var publishTime = DateTimeOffset.UtcNow;
        var stagingPath = AddonPackager.GetStagingPath(addonsRoot, publishedFileId);

        if (Directory.Exists(stagingPath))
        {
            Directory.Delete(stagingPath, recursive: true);
        }

        AddonVpk.Stage(addonPath, request.Selection, stagingPath, publishedFileId.ToString(CultureInfo.InvariantCulture), request.AddonName, request.Title,
            publishTime, limit, CancellationToken.None);

        var handle = ugc.StartItemUpdate(SteamConnection.AppId, publishedFileId);
        ugc.SetItemTitle(handle, request.Title);

        if ((request.Description ?? (creating ? string.Empty : null)) is string description)
        {
            ugc.SetItemDescription(handle, description);
        }

        ugc.SetItemVisibility(handle, ToSteam(request.Visibility));

        if (request.PreviewPath != null)
        {
            ugc.SetItemPreview(handle, WriteThumbnail(request.PreviewPath, publishedFileId, publishTime));
        }

        ugc.SetItemContent(handle, stagingPath);

        // Valve's tool always tags items "CS2" (in-game workshop browsing may rely on it), so it is added when missing.
        // Valve's other default ("Map") is left out: most of these addons are not maps.
        IReadOnlyList<string>? tags = request.Tags.Count > 0 || creating ? WithCs2Tag(request.Tags) : null;

        if (tags != null)
        {
            ugc.SetItemTags(handle, tags);
        }

        var submitted = await client.WaitForCallResultAsync<SubmitItemUpdateResult>(ugc.SubmitItemUpdate(handle, request.ChangeNote), () =>
        {
            var update = ugc.GetItemUpdateProgress(handle);

            if (update.BytesTotal > 0)
            {
                progress.Report((float)update.BytesProcessed / update.BytesTotal);
            }
        }).ConfigureAwait(false);

        if (submitted.Result != EResult.OK)
        {
            throw new InvalidOperationException($"SubmitItemUpdate failed: {submitted.Result}");
        }

        return new UploadResult(publishedFileId, needsAgreement || submitted.UserNeedsToAcceptWorkshopLegalAgreement);
    }

    /// <summary>
    /// The thumbnail as WorkshopManager writes it: Steam takes previews under 1 MB, so it becomes a JPEG (quality 75),
    /// except a gif, which goes unchanged (ValidateThumbnailImage checked its size).
    /// </summary>
    private static string WriteThumbnail(string sourcePath, ulong publishedFileId, DateTimeOffset time)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"workshopupload_{publishedFileId}");
        Directory.CreateDirectory(directory);
        var stamp = time.ToUnixTimeSeconds().ToString("x", CultureInfo.InvariantCulture);

        if (IsGif(sourcePath))
        {
            var gifPath = Path.Combine(directory, $"thumbnail_{stamp}.gif");
            File.Copy(sourcePath, gifPath, overwrite: true);
            return gifPath;
        }

        ImageResult image;

        using (var input = File.OpenRead(sourcePath))
        {
            image = ImageResult.FromStream(input, StbImageSharp.ColorComponents.RedGreenBlue);
        }

        var path = Path.Combine(directory, $"thumbnail_{stamp}.jpg");

        using var output = File.Create(path);
        new ImageWriter().WriteJpg(image.Data, image.Width, image.Height, StbImageWriteSharp.ColorComponents.RedGreenBlue, output, WorkshopManager.ThumbnailJpegQuality);

        return path;
    }

    private static bool IsGif(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];

        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header.SequenceEqual("GIF8"u8);
    }

    /// <summary>ERemoteStoragePublishedFileVisibility: 0 public, 1 friends only, 2 private, 3 unlisted.</summary>
    private static ERemoteStoragePublishedFileVisibility ToSteam(Visibility visibility) => visibility switch
    {
        Visibility.Public => (ERemoteStoragePublishedFileVisibility)0,
        Visibility.FriendsOnly => (ERemoteStoragePublishedFileVisibility)1,
        Visibility.Unlisted => (ERemoteStoragePublishedFileVisibility)3,
        _ => (ERemoteStoragePublishedFileVisibility)2,
    };

    /// <summary>The given tags with "CS2" first, added when missing (case-insensitive), duplicates and blanks dropped.</summary>
    internal static IReadOnlyList<string> WithCs2Tag(IReadOnlyList<string> tags)
    {
        var result = new List<string> { "CS2" };

        foreach (var tag in tags)
        {
            var trimmed = tag.Trim();

            if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}

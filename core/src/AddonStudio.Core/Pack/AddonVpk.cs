using System.Globalization;
using System.Text.RegularExpressions;
using AddonStudio.Core.Contracts;
using CS2WorkshopManager;
using ValveKeyValue;
using ValvePak;

namespace AddonStudio.Core.Pack;

/// <summary>
/// Our copy of AddonPackager.Pack and Stage (CS2WorkshopManager 1.5.0): the same file selection (CollectFiles),
/// ValvePak Package with 100 MiB chunks and multiChunk entries, &lt;name&gt;_dir.vpk plus chunks plus publish_data.txt
/// in the same format. The differences: the total size cap is a parameter instead of the 3 GiB constant, it writes
/// into any folder, and that folder is never wiped (only the files this writes are replaced).
/// </summary>
public static partial class AddonVpk
{
    /// <summary>Writes &lt;outDir&gt;/&lt;vpkName&gt;_dir.vpk, its chunks and publish_data.txt. Returns the full paths written.</summary>
    public static List<string> Stage(
        string addonPath,
        string gameInfoPath,
        string outDir,
        string vpkName,
        string sourceFolder,
        string title,
        DateTimeOffset publishTime,
        AddonRules? rules,
        long limitBytes,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        RemoveStale(outDir, vpkName);

        var directoryFile = Path.Combine(outDir, $"{vpkName}_dir.vpk");
        Pack(addonPath, gameInfoPath, directoryFile, rules, limitBytes, ct);
        WritePublishData(Path.Combine(outDir, AddonPackager.PublishDataFileName), title, sourceFolder, publishTime);

        return OwnFiles(outDir, vpkName);
    }

    /// <summary>AddonPackager.Pack with limitBytes in place of MaxTotalSize. Returns the packed build-relative paths.</summary>
    public static List<string> Pack(string addonPath, string gameInfoPath, string outputDirectoryFile, AddonRules? rules, long limitBytes, CancellationToken ct = default)
    {
        addonPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(addonPath));

        var files = AddonPackager.CollectFiles(addonPath, gameInfoPath, rules);

        using var package = new Package();
        package.WriteChunkSize = AddonPackager.ChunkSize;

        var totalSize = 0L;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var size = file.Length;

            if (totalSize >= limitBytes || totalSize + size >= limitBytes)
            {
                throw new AddonStudioException(
                    ErrorCodes.InvalidParams,
                    $"The pack is over the size cap of {AddonContents.FormatSize(limitBytes)} at \"{AddonPackager.GetRelativePath(addonPath, file.FullName)}\".",
                    new { limitBytes, totalSize = files.Sum(entry => entry.Length) });
            }

            if (size >= AddonPackager.MaxFileSize)
            {
                throw new AddonStudioException(
                    ErrorCodes.InvalidParams,
                    $"\"{AddonPackager.GetRelativePath(addonPath, file.FullName)}\" is over the 2 GB single file limit of VPK.",
                    new { size });
            }

            package.AddFile(AddonPackager.GetRelativePath(addonPath, file.FullName), File.ReadAllBytes(file.FullName), multiChunk: true);
            totalSize += size;
        }

        package.Write(outputDirectoryFile);

        return [.. files.Select(file => AddonPackager.GetRelativePath(addonPath, file.FullName))];
    }

    /// <summary>publish_data.txt exactly as AddonPackager.Stage writes it.</summary>
    public static void WritePublishData(string path, string title, string sourceFolder, DateTimeOffset publishTime)
    {
        var publishData = KVObject.Collection();
        publishData.Add("title", title);
        publishData.Add("source_folder", sourceFolder);
        publishData.Add("publish_time", publishTime.ToUnixTimeSeconds());
        publishData.Add("publish_time_readable", publishTime.ToLocalTime().ToString("MM/dd/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture));

        using var stream = File.Create(path);
        KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Serialize(stream, publishData, "publish_data");
    }

    /// <summary>The files a stage of vpkName leaves in a folder: _dir.vpk, _NNN.vpk chunks, publish_data.txt.</summary>
    public static List<string> OwnFiles(string outDir, string vpkName)
    {
        if (!Directory.Exists(outDir))
        {
            return [];
        }

        var chunk = ChunkName();
        var prefix = vpkName + "_";

        return Directory.EnumerateFiles(outDir)
            .Where(file =>
            {
                var name = Path.GetFileName(file);

                return name.Equals(AddonPackager.PublishDataFileName, StringComparison.OrdinalIgnoreCase)
                    || (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && chunk.IsMatch(name[prefix.Length..]));
            })
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void RemoveStale(string outDir, string vpkName)
    {
        foreach (var file in OwnFiles(outDir, vpkName))
        {
            File.Delete(file);
        }
    }

    [GeneratedRegex(@"^(dir|\d{3})\.vpk$", RegexOptions.IgnoreCase)]
    private static partial Regex ChunkName();
}

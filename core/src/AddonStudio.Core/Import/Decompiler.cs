using AddonStudio.Core.Util;
using ValveResourceFormat;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;

namespace AddonStudio.Core.Import;

/// <summary>
/// Turns the compiled types that come back cleanly with ValveResourceFormat into sources (SPEC 6.4): panorama
/// vxml_c/vcss_c/vjs_c -> xml/css/js, vsvg_c -> svg, panorama images *_png.vtex_c -> png, vsnd_c -> wav/mp3,
/// vsndevts_c -> vsndevts.
/// </summary>
public static class Decompiler
{
    private static readonly string[] TextTypes = [".vxml_c", ".vcss_c", ".vjs_c", ".vsvg_c", ".vsndevts_c"];

    /// <summary>
    /// The content-relative source a decompile of this build-relative file would write, null when the type is not one
    /// we decompile. Sounds are opened to see whether they are wav or mp3 (null when that fails or it is neither).
    /// </summary>
    public static string? SourceFor(string buildRel, string compiledPath)
    {
        var extension = Paths.Extension(buildRel);
        var candidates = AssetRules.SourceCandidates(buildRel);

        if (candidates.Count == 0)
        {
            return null;
        }

        if (TextTypes.Contains(extension))
        {
            return candidates[0];
        }

        switch (extension)
        {
            case ".vtex_c":
                // only panorama images that were pngs: a jpg/tga/psd source can not be rebuilt from the decoded pixels
                return candidates.FirstOrDefault(candidate => Paths.Extension(candidate) == ".png");
            case ".vsnd_c":
                var soundExtension = SoundExtension(compiledPath);

                return soundExtension == null ? null : candidates.FirstOrDefault(candidate => Paths.Extension(candidate) == soundExtension);
            default:
                return null;
        }
    }

    /// <summary>".wav" or ".mp3" for a compiled sound, null when it can not be read or is another format.</summary>
    public static string? SoundExtension(string compiledPath)
    {
        try
        {
            using var resource = new Resource { FileName = compiledPath };
            resource.Read(compiledPath);

            return resource.DataBlock is Sound sound
                ? sound.SoundType switch
                {
                    Sound.AudioFileType.WAV => ".wav",
                    Sound.AudioFileType.MP3 => ".mp3",
                    _ => null,
                }
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Debug($"Could not read {compiledPath} as a sound: {exception.Message}");

            return null;
        }
    }

    /// <summary>The source bytes for a compiled file. Throws when it does not decompile cleanly.</summary>
    public static byte[] Decompile(string compiledPath, string targetExtension)
    {
        using var resource = new Resource { FileName = compiledPath };
        resource.Read(compiledPath);

        byte[]? data;

        switch (resource.DataBlock)
        {
            case Texture texture:
                if (targetExtension != ".png")
                {
                    throw new InvalidDataException($"A texture decompiles to png, not {targetExtension}.");
                }

                using (var bitmap = texture.GenerateBitmap())
                {
                    data = TextureExtract.ToPngImage(bitmap);
                }

                break;
            case Sound sound:
                var actual = sound.SoundType switch
                {
                    Sound.AudioFileType.WAV => ".wav",
                    Sound.AudioFileType.MP3 => ".mp3",
                    _ => sound.SoundType.ToString().ToLowerInvariant(),
                };

                if (actual != targetExtension)
                {
                    throw new InvalidDataException($"The sound is {actual}, expected {targetExtension}.");
                }

                data = sound.GetSound();
                break;
            default:
                using (var content = FileExtract.Extract(resource, new NullFileLoader()))
                {
                    data = content.Data;
                }

                break;
        }

        if (data == null || data.Length == 0)
        {
            throw new InvalidDataException("Nothing came out of the decompile.");
        }

        return data;
    }
}

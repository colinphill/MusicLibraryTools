using MusicFileUtilities;
using MusicLibraryTools;
using MusicLibrary.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace MusicLibrary.Core.Services;

/// <inheritdoc cref="IArtworkService"/>
public sealed class ArtworkService : IArtworkService
{
    private readonly IReindexService? _reindex;
    private readonly IFileMutationCoordinator _mutations;
    private readonly IItunesMediaMutationService? _itunes;
    private readonly IMediaFormatRegistry _formats;
    private readonly IAppSettings? _settings;

    // The reindex service is optional so this service can be constructed standalone (unit tests).
    public ArtworkService(
        IReindexService? reindex = null,
        IFileMutationCoordinator? mutations = null,
        IItunesMediaMutationService? itunes = null,
        IMediaFormatRegistry? formats = null,
        IAppSettings? settings = null)
    {
        _reindex = reindex;
        _mutations = mutations ?? FileMutationCoordinator.Shared;
        _itunes = itunes;
        _formats = formats ?? MediaFormatRegistry.Default;
        _settings = settings;
    }

    // Every audio format the toolkit tags can carry embedded artwork, so writability is decided by
    // extension — no need to open/parse the file just to answer.
    public bool SupportsWrite(string musicPath)
    {
        ArtworkPolicyContext context = ResolvePolicy(musicPath);
        if (!context.Permissions.HasFlag(LibraryRootPermissions.WriteArtwork) ||
            context.Policy.Storage == LibraryArtworkStorage.None)
            return false;
        return context.Policy.Storage is LibraryArtworkStorage.Sidecar or
                   LibraryArtworkStorage.Both ||
               _formats.SupportsPath(musicPath, MediaFormatCapabilities.WriteArtwork);
    }

    public async Task<ArtworkOpResult> SetCoverFromFileAsync(string musicPath, string imagePath, int maxDimension = 0, CancellationToken ct = default)
    {
        ArtworkPolicyContext context = ResolvePolicy(musicPath);
        if (PolicyError(context, musicPath) is { } error)
            return ArtworkOpResult.Fail(error);
        PreparedArtwork prepared;
        try
        {
            prepared = await Task.Run(() => PrepareArtwork(
                File.ReadAllBytes(imagePath), MimeFromPath(imagePath), context.Policy,
                maxDimension), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return ArtworkOpResult.Fail(ex.Message); }

        return await ApplyItunesAwareAsync(musicPath,
            () => Task.Run(() => ApplyPreparedArtwork(
                musicPath,
                [new ArtworkInput(ID3v2Util.APICType.FrontCover, prepared.MimeType,
                    prepared.Data)],
                [prepared],
                context.Policy,
                replaceAllEmbedded: false), ct), ct);
    }

    public async Task<ArtworkOpResult> ScrubAsync(string musicPath, int maxDimension, int quality = 90, CancellationToken ct = default)
    {
        ArtworkPolicyContext context = ResolvePolicy(musicPath);
        if (PolicyError(context, musicPath) is { } error)
            return ArtworkOpResult.Fail(error);
        return await ApplyItunesAwareAsync(musicPath, () => Task.Run(() =>
        {
            try
            {
                var file = MediaFile.GetFile(musicPath);
                var current = file.Tags.FirstOrDefault()?.GetImageMetadata().FirstOrDefault();
                if (current is null || current.Data.Length == 0)
                    return Failed("No embedded artwork to scrub.");

                LibraryArtworkPolicy scrubPolicy = context.Policy with
                {
                    JpegQuality = Math.Clamp(quality, 1, 100),
                };
                PreparedArtwork prepared = PrepareArtwork(
                    current.Data, current.ImageType, scrubPolicy, maxDimension);
                return ApplyPreparedArtwork(
                    file,
                    musicPath,
                    [new ArtworkInput(ID3v2Util.APICType.FrontCover,
                        prepared.MimeType, prepared.Data, current.Description)],
                    [prepared],
                    scrubPolicy,
                    replaceAllEmbedded: false);
            }
            catch (Exception ex)
            {
                return Failed(ex.Message);
            }
        }, ct), ct);
    }

    public async Task<ArtworkOpResult> RemoveAsync(string musicPath, CancellationToken ct = default)
    {
        ArtworkPolicyContext context = ResolvePolicy(musicPath);
        if (PolicyError(context, musicPath) is { } error)
            return ArtworkOpResult.Fail(error);
        return await ApplyItunesAwareAsync(musicPath, () => Task.Run(() =>
        {
            try
            {
                IMediaFile? file = null;
                if (WritesEmbedded(context.Policy))
                {
                    file = MediaFile.GetFile(musicPath);
                    if (ResolveWriter(file) is not { } writer)
                        return Failed("Artwork writing is not supported for this format.");
                    writer.RemoveImages();
                    file.SaveTags();
                }
                if (WritesSidecars(context.Policy))
                    RemoveManagedSidecars(musicPath, context.Policy);
                var result = new ArtworkOpResult { Success = true };
                return file is null
                    ? new SavedArtworkResult(result)
                    : Succeeded(result, file);
            }
            catch (Exception ex)
            {
                return Failed(ex.Message);
            }
        }, ct), ct);
    }

    public Task<PreparedImage?> PrepareFromFileAsync(string imagePath, int maxDimension = 0, CancellationToken ct = default)
        => Task.Run<PreparedImage?>(() =>
        {
            try
            {
                var (jpeg, w, h) = Encode(File.ReadAllBytes(imagePath), maxDimension);
                return new PreparedImage(jpeg, "image/jpeg", w, h);
            }
            catch { return null; }
        }, ct);

    public Task<PreparedImage?> PrepareFromBytesAsync(byte[] data, int maxDimension = 0, int quality = 90, CancellationToken ct = default)
        => Task.Run<PreparedImage?>(() =>
        {
            try
            {
                var (jpeg, w, h) = Encode(data, maxDimension, quality);
                return new PreparedImage(jpeg, "image/jpeg", w, h);
            }
            catch { return null; }
        }, ct);

    public async Task<ArtworkOpResult> SaveImagesAsync(string musicPath, IReadOnlyList<ArtworkInput> images, CancellationToken ct = default)
    {
        ArtworkPolicyContext context = ResolvePolicy(musicPath);
        if (PolicyError(context, musicPath) is { } error)
            return ArtworkOpResult.Fail(error);
        return await ApplyItunesAwareAsync(musicPath, () => Task.Run(() =>
        {
            try
            {
                ArtworkInput[] selected = context.Policy.Roles ==
                                          LibraryArtworkRoleSelection.FrontCoverOnly
                    ? images.Where(image => image.Type == ID3v2Util.APICType.FrontCover).ToArray()
                    : images.ToArray();
                IMediaFile? file = WritesEmbedded(context.Policy)
                    ? MediaFile.GetFile(musicPath)
                    : null;

                // A null description means the caller did not edit that property. Preserve it when
                // the image bytes came from the existing tag; an explicit empty string still clears it.
                var existingImages = file?.Tags.FirstOrDefault()?.GetImageMetadata().ToList() ?? [];
                var consumed = new bool[existingImages.Count];
                string PreservedDescription(ArtworkInput input)
                {
                    if (input.Description is not null)
                        return input.Description;
                    for (var index = 0; index < existingImages.Count; index++)
                    {
                        if (!consumed[index] && existingImages[index].Data.AsSpan().SequenceEqual(input.Data))
                        {
                            consumed[index] = true;
                            return existingImages[index].Description ?? "";
                        }
                    }
                    return "";
                }

                ArtworkInput[] effective = selected.Select(input => input with
                    { Description = PreservedDescription(input) }).ToArray();
                LibraryArtworkPolicy savePolicy = context.LegacyBehavior
                    ? context.Policy with
                    {
                        Encoding = LibraryArtworkEncoding.PreserveSource,
                        MaximumDimension = 0,
                        MaximumEncodedBytes = 0,
                    }
                    : context.Policy;
                PreparedArtwork[] prepared = effective.Select(input =>
                    PrepareArtwork(input.Data, input.MimeType, savePolicy, 0)).ToArray();
                ArtworkInput[] converted = effective.Select((input, index) => input with
                    {
                        Data = prepared[index].Data,
                        MimeType = prepared[index].MimeType,
                    }).ToArray();
                return ApplyPreparedArtwork(
                    file, musicPath, converted, prepared, savePolicy,
                    replaceAllEmbedded: true);
            }
            catch (Exception ex)
            {
                return Failed(ex.Message);
            }
        }, ct), ct);
    }

    private sealed record SavedArtworkResult(ArtworkOpResult Result, IMediaFile? SavedFile = null);

    private static SavedArtworkResult Failed(string error) => new(ArtworkOpResult.Fail(error));
    private static SavedArtworkResult Succeeded(ArtworkOpResult result, IMediaFile file) => new(result, file);

    private async Task<ArtworkOpResult> ApplyItunesAwareAsync(
        string musicPath,
        Func<Task<SavedArtworkResult>> write,
        CancellationToken ct)
    {
        using IDisposable mutation = await _mutations.AcquireAsync(musicPath, ct);
        await using IItunesMediaMutationSession? itunesSession = _itunes is null
            ? null
            : await _itunes.BeginAsync([musicPath], backupFiles: true, ct);
        SavedArtworkResult saved = await write().ConfigureAwait(false);
        if (saved.Result.Success && itunesSession is not null)
        {
            await itunesSession.CommitAsync(
                [ItunesMediaMutation.Refresh(musicPath)], CancellationToken.None);
            await itunesSession.CompleteAsync(CancellationToken.None);
        }
        return await ReindexIfSaved(saved, musicPath);
    }

    // Keep the cache in sync with what we just wrote to disk.
    private async Task<ArtworkOpResult> ReindexIfSaved(SavedArtworkResult saved, string musicPath)
    {
        var result = saved.Result;
        if (result.Success && _reindex is not null)
        {
            try
            {
                if (saved.SavedFile is not null)
                    await _reindex.ReindexFileAsync(musicPath, saved.SavedFile, CancellationToken.None);
                else
                    await _reindex.ReindexFileAsync(musicPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                return result with { CacheError = ex.Message };
            }
        }
        return result;
    }

    private sealed record ArtworkPolicyContext(
        LibraryArtworkPolicy Policy,
        LibraryRootPermissions Permissions,
        bool LegacyBehavior);

    internal sealed record PreparedArtwork(
        byte[] Data,
        string MimeType,
        string Extension,
        int Width,
        int Height);

    private ArtworkPolicyContext ResolvePolicy(string musicPath)
    {
        AppConfigurationSnapshot? snapshot = _settings?.GetSnapshot();
        LibraryConfiguration? configuration = snapshot?.Configuration;
        if (configuration is null)
        {
            LibraryProfile legacy = LibraryProfilePresets.Create(
                LibraryProfilePreset.LegacyMusicLibraryTools);
            return new(legacy.Artwork, LibraryRootPermissions.WriteArtwork, true);
        }

        string? configPath = snapshot?.ConfigPath;
        string fullMusicPath = Path.GetFullPath(musicPath);
        var matchingRoots = configuration.IndexLocations
            .Select(location => (Location: location,
                FullPath: ResolveRootPath(location.Target, configPath)))
            .Where(item => IsWithinRoot(fullMusicPath, item.FullPath))
            .OrderByDescending(item => item.FullPath.Length)
            .ToArray();
        LibraryIndexLocation? root = matchingRoots.FirstOrDefault().Location;
        LibraryProfile profile = root is null
            ? configuration.ActiveProfile
            : configuration.GetEffectiveProfile(root);
        LibraryRootPermissions permissions = matchingRoots.Length == 0
            ? LibraryRootPermissions.None
            : matchingRoots.Select(item => item.Location.Permissions)
                .Aggregate(LibraryRootPermissions.All, (allowed, current) =>
                    allowed & current);
        return new(profile.Artwork,
            permissions,
            profile.Preset == LibraryProfilePreset.LegacyMusicLibraryTools);
    }

    private static string ResolveRootPath(string path, string? configurationPath)
    {
        if (Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(configurationPath))
            return Path.GetFullPath(path);
        return Path.GetFullPath(path, Path.GetDirectoryName(configurationPath)!);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedPath, normalizedRoot, comparison) ||
               normalizedPath.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private string? PolicyError(ArtworkPolicyContext context, string musicPath)
    {
        if (!context.Permissions.HasFlag(LibraryRootPermissions.WriteArtwork))
            return "The effective library root policy does not permit artwork writes.";
        if (context.Policy.Storage == LibraryArtworkStorage.None)
            return "The effective artwork policy disables artwork storage.";
        if (WritesEmbedded(context.Policy) &&
            !_formats.SupportsPath(musicPath, MediaFormatCapabilities.WriteArtwork))
            return "The effective artwork policy requires embedded artwork, but this media " +
                   "format does not support artwork writes.";
        return null;
    }

    internal static bool WritesEmbedded(LibraryArtworkPolicy policy) =>
        policy.Storage is LibraryArtworkStorage.Embedded or LibraryArtworkStorage.Both;

    internal static bool WritesSidecars(LibraryArtworkPolicy policy) =>
        policy.Storage is LibraryArtworkStorage.Sidecar or LibraryArtworkStorage.Both;

    private static SavedArtworkResult ApplyPreparedArtwork(
        string musicPath,
        IReadOnlyList<ArtworkInput> inputs,
        IReadOnlyList<PreparedArtwork> prepared,
        LibraryArtworkPolicy policy,
        bool replaceAllEmbedded)
    {
        IMediaFile? file = WritesEmbedded(policy) ? MediaFile.GetFile(musicPath) : null;
        return ApplyPreparedArtwork(
            file, musicPath, inputs, prepared, policy, replaceAllEmbedded);
    }

    private static SavedArtworkResult ApplyPreparedArtwork(
        IMediaFile? file,
        string musicPath,
        IReadOnlyList<ArtworkInput> inputs,
        IReadOnlyList<PreparedArtwork> prepared,
        LibraryArtworkPolicy policy,
        bool replaceAllEmbedded)
    {
        if (WritesEmbedded(policy))
        {
            if (file is null || ResolveWriter(file) is not { } writer)
                return Failed("Artwork writing is not supported for this format.");
            if (replaceAllEmbedded)
                writer.SetImages(inputs.Select(input => new ArtworkImage(
                    input.Type, input.MimeType, input.Description ?? "", input.Data)).ToList());
            else if (inputs.Count > 0)
                writer.SetFrontCover(inputs[0].Data, inputs[0].MimeType);
            file.SaveTags();
        }

        if (WritesSidecars(policy))
        {
            if (replaceAllEmbedded)
                RemoveManagedSidecars(musicPath, policy);
            WriteSidecars(musicPath, inputs, prepared, policy);
        }

        PreparedArtwork? first = prepared.FirstOrDefault();
        var result = new ArtworkOpResult
        {
            Success = true,
            Width = first?.Width ?? 0,
            Height = first?.Height ?? 0,
            Size = prepared.Sum(item => item.Data.Length),
        };
        return file is null
            ? new SavedArtworkResult(result)
            : Succeeded(result, file);
    }

    private static void WriteSidecars(
        string musicPath,
        IReadOnlyList<ArtworkInput> inputs,
        IReadOnlyList<PreparedArtwork> prepared,
        LibraryArtworkPolicy policy)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(musicPath))!;
        Directory.CreateDirectory(directory);
        for (var index = 0; index < prepared.Count; index++)
        {
            string fileName = SidecarFileName(
                policy, inputs[index].Type, index + 1, prepared[index].Extension);
            string destination = Path.Combine(directory, fileName);
            string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, prepared[index].Data);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static void RemoveManagedSidecars(string musicPath, LibraryArtworkPolicy policy)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(musicPath))!;
        if (!Directory.Exists(directory)) return;
        string[] roles = ["cover", "back", "booklet", "disc",
            .. Enumerable.Range(1, 32).Select(index => $"artwork-{index}")];
        string[] extensions = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];
        foreach (string role in roles)
            foreach (string extension in extensions)
            {
                string fileName = policy.SidecarFileNameTemplate
                    .Replace("{Role}", role, StringComparison.OrdinalIgnoreCase)
                    .Replace("{Extension}", extension, StringComparison.OrdinalIgnoreCase);
                string path = Path.Combine(directory, fileName);
                if (File.Exists(path)) File.Delete(path);
            }
    }

    internal static string RoleName(ID3v2Util.APICType role, int index) => role switch
    {
        ID3v2Util.APICType.FrontCover => "cover",
        ID3v2Util.APICType.BackCover => "back",
        ID3v2Util.APICType.LeafletPage => "booklet",
        ID3v2Util.APICType.Media => "disc",
        _ => $"artwork-{index}",
    };

    internal static string SidecarFileName(
        LibraryArtworkPolicy policy,
        ID3v2Util.APICType role,
        int index,
        string extension) => policy.SidecarFileNameTemplate
        .Replace("{Role}", RoleName(role, index), StringComparison.OrdinalIgnoreCase)
        .Replace("{Extension}", extension, StringComparison.OrdinalIgnoreCase);

    internal static PreparedArtwork PrepareArtwork(
        byte[] source,
        string? declaredMimeType,
        LibraryArtworkPolicy policy,
        int requestedMaximumDimension)
    {
        IImageFormat sourceFormat = Image.DetectFormat(source) ??
            throw new InvalidDataException("The artwork image format is not recognized.");
        using Image image = Image.Load(source);
        int maximumDimension = EffectiveMaximum(
            requestedMaximumDimension, policy.MaximumDimension);
        bool resize = maximumDimension > 0 &&
                      (image.Width > maximumDimension || image.Height > maximumDimension);
        byte[] output;
        string mimeType;
        string extension;
        if (policy.Encoding == LibraryArtworkEncoding.PreserveSource && !resize)
        {
            output = source.ToArray();
            mimeType = NormalizeMimeType(declaredMimeType, sourceFormat.DefaultMimeType);
            extension = ExtensionForMime(mimeType,
                sourceFormat.FileExtensions.FirstOrDefault());
        }
        else
        {
            ArtworkImageProcessor.ResizeToFit(image, maximumDimension);
            using var stream = new MemoryStream();
            switch (policy.Encoding)
            {
                case LibraryArtworkEncoding.Jpeg:
                    image.Save(stream, new JpegEncoder { Quality = policy.JpegQuality });
                    mimeType = "image/jpeg";
                    extension = ".jpg";
                    break;
                case LibraryArtworkEncoding.Png:
                    image.Save(stream, new PngEncoder());
                    mimeType = "image/png";
                    extension = ".png";
                    break;
                default:
                    image.Save(stream, sourceFormat);
                    mimeType = sourceFormat.DefaultMimeType;
                    extension = ExtensionForMime(
                        mimeType, sourceFormat.FileExtensions.FirstOrDefault());
                    break;
            }
            output = stream.ToArray();
        }
        if (policy.MaximumEncodedBytes > 0 &&
            output.Length > policy.MaximumEncodedBytes)
            throw new InvalidDataException(
                $"Artwork is {output.Length:N0} bytes, exceeding the active policy limit of " +
                $"{policy.MaximumEncodedBytes:N0} bytes.");
        return new(output, mimeType, extension, image.Width, image.Height);
    }

    private static int EffectiveMaximum(int requested, int policyMaximum)
    {
        if (requested <= 0) return policyMaximum;
        if (policyMaximum <= 0) return requested;
        return Math.Min(requested, policyMaximum);
    }

    private static string NormalizeMimeType(string? declared, string detected) =>
        !string.IsNullOrWhiteSpace(declared) &&
        declared.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? declared.ToLowerInvariant()
            : detected;

    private static string MimeFromPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/jpeg",
        };

    private static string ExtensionForMime(string mimeType, string? detectedExtension) =>
        mimeType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ when !string.IsNullOrWhiteSpace(detectedExtension) =>
                "." + detectedExtension.TrimStart('.').ToLowerInvariant(),
            _ => ".img",
        };

    // The writer may live on the IMediaFile (MP3/DSF via ID3v2Tag, FLAC/Ogg via VorbisComments,
    // MP4 directly) or on the tag object (WavPack's APETag).
    private static IArtworkWriter? ResolveWriter(IMediaFile file)
        => file as IArtworkWriter ?? file.Tags.FirstOrDefault() as IArtworkWriter;

    private static (byte[] Jpeg, int Width, int Height) Encode(byte[] source, int maxDimension, int quality = 90)
    {
        using var image = Image.Load(source);
        ArtworkImageProcessor.ResizeToFit(image, maxDimension);

        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality });
        return (ms.ToArray(), image.Width, image.Height);
    }
}

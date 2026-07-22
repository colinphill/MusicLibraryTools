using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace MusicLibraryTools.Build;

public sealed class SyncerDaemonResource
{
    internal SyncerDaemonResource(string abi)
    {
        Abi = abi;
        FileName = "syncerd";
        ResourceName = $"Syncer.Servers.{abi}.syncerd";
    }

    public string Abi { get; }
    public string FileName { get; }
    public string ResourceName { get; }
}

public static class SyncerResourceVerifier
{
    private static readonly SyncerDaemonResource[] Resources =
    [
        new("arm64-v8a"),
        new("armeabi-v7a"),
        new("x86_64"),
        new("x86"),
    ];

    public static IReadOnlyList<SyncerDaemonResource> RequiredResources => Resources;

    public static void Verify(string assemblyPath, string serverRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new InvalidDataException($"'{assemblyPath}' is not a managed assembly.");

        MetadataReader metadata = peReader.GetMetadataReader();
        var manifests = new Dictionary<string, ManifestResource>(StringComparer.Ordinal);
        foreach (ManifestResourceHandle handle in metadata.ManifestResources)
        {
            ManifestResource resource = metadata.GetManifestResource(handle);
            manifests.Add(metadata.GetString(resource.Name), resource);
        }

        string[] actualNames = manifests.Keys
            .Where(name => name.StartsWith("Syncer.Servers.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        VerifyResourceNames(assemblyPath, actualNames);

        DirectoryEntry directory = peReader.PEHeaders.CorHeader?.ResourcesDirectory
            ?? throw new InvalidDataException($"'{assemblyPath}' has no CLR header.");
        if (directory.Size == 0)
            throw new InvalidDataException($"'{assemblyPath}' contains no managed resources.");
        byte[] section = peReader.GetSectionData(directory.RelativeVirtualAddress)
            .GetContent(0, directory.Size).ToArray();

        foreach (SyncerDaemonResource expected in Resources)
        {
            if (!manifests.TryGetValue(expected.ResourceName, out ManifestResource resource) ||
                !resource.Implementation.IsNil)
            {
                throw new InvalidDataException(
                    $"'{assemblyPath}' is missing embedded resource '{expected.ResourceName}'.");
            }

            int offset = checked((int)resource.Offset);
            if (offset < 0 || offset > section.Length - sizeof(int))
                throw InvalidOffset(assemblyPath, expected.ResourceName);
            int length = BinaryPrimitives.ReadInt32LittleEndian(section.AsSpan(offset, sizeof(int)));
            int payloadOffset = checked(offset + sizeof(int));
            if (length < 0 || payloadOffset > section.Length - length)
                throw InvalidOffset(assemblyPath, expected.ResourceName);

            string sourcePath = Path.Combine(serverRoot, expected.Abi, expected.FileName);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Android syncer daemon is missing: {sourcePath}", sourcePath);
            byte[] source = File.ReadAllBytes(sourcePath);
            ReadOnlySpan<byte> embedded = section.AsSpan(payloadOffset, length);
            if (source.Length == 0 || embedded.Length == 0)
            {
                throw new InvalidDataException(
                    $"Android syncer daemon '{expected.ResourceName}' must not be empty.");
            }
            if (!embedded.SequenceEqual(source))
            {
                string sourceHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
                string embeddedHash = Convert.ToHexString(SHA256.HashData(embedded)).ToLowerInvariant();
                throw new InvalidDataException(
                    $"Embedded resource '{expected.ResourceName}' does not match '{sourcePath}' " +
                    $"(embedded {embeddedHash}, source {sourceHash}).");
            }
        }
    }

    internal static void VerifyResourceNames(string assemblyPath, IReadOnlyCollection<string> actualNames)
    {
        string[] expectedNames = Resources.Select(resource => resource.ResourceName).ToArray();
        if (actualNames.Count != expectedNames.Length ||
            !actualNames.ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames))
        {
            throw new InvalidDataException(
                $"'{assemblyPath}' must contain exactly these Syncer server resources: " +
                string.Join(", ", expectedNames) + ". Found: " +
                (actualNames.Count == 0 ? "none" : string.Join(", ", actualNames)) + ".");
        }
    }

    private static InvalidDataException InvalidOffset(string assemblyPath, string resourceName) =>
        new($"'{assemblyPath}' has an invalid data offset for embedded resource '{resourceName}'.");
}

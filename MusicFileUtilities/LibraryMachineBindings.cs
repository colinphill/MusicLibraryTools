#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using System.Xml.Linq;

namespace MusicLibraryTools
{
    public sealed record LibraryExportTransportBinding(
        string ProfileId,
        string Destination,
        IReadOnlyDictionary<string, string> Options);

    /// <summary>
    /// Machine-local paths associated with a portable library policy. The portable configuration
    /// references this document with <c>&lt;MachineBindings File="..." /&gt;</c>; roots are joined
    /// by their stable IDs instead of by path or list position.
    /// </summary>
    public sealed class LibraryMachineBindings
    {
        public const int CurrentSchemaVersion = 1;

        private readonly Dictionary<Guid, string> rootPaths_;
        private readonly Dictionary<string, LibraryExportTransportBinding> exportTransports_;

        private LibraryMachineBindings(
            string sourcePath,
            Guid libraryId,
            Dictionary<Guid, string> rootPaths,
            string? databaseFile,
            string? ffmpegPath,
            string? wavpackPath,
            string? monkeysAudioPath,
            string? itunesLibraryPath,
            Dictionary<string, LibraryExportTransportBinding> exportTransports)
        {
            SourcePath = sourcePath;
            LibraryId = libraryId;
            rootPaths_ = rootPaths;
            DatabaseFile = databaseFile;
            FfmpegPath = ffmpegPath;
            WavpackPath = wavpackPath;
            MonkeysAudioPath = monkeysAudioPath;
            ItunesLibraryPath = itunesLibraryPath;
            exportTransports_ = exportTransports;
        }

        public string SourcePath { get; }
        public Guid LibraryId { get; }
        public IReadOnlyDictionary<Guid, string> RootPaths => rootPaths_;
        public string? DatabaseFile { get; }
        public string? FfmpegPath { get; }
        public string? WavpackPath { get; }
        public string? MonkeysAudioPath { get; }
        public string? ItunesLibraryPath { get; }
        public IReadOnlyDictionary<string, LibraryExportTransportBinding> ExportTransports =>
            new ReadOnlyDictionary<string, LibraryExportTransportBinding>(exportTransports_);

        public static LibraryMachineBindings? LoadReferenced(
            XElement configurationRoot,
            string configurationPath,
            Guid expectedLibraryId)
        {
            ArgumentNullException.ThrowIfNull(configurationRoot);
            ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);

            XElement[] references = configurationRoot.Elements("MachineBindings").ToArray();
            if (references.Length == 0)
                return null;
            if (references.Length > 1)
                throw new InvalidDataException(
                    "Only one <MachineBindings> reference may be configured.");

            string? file = Clean((string?)references[0].Attribute("File"));
            if (file is null)
                throw new InvalidDataException(
                    "<MachineBindings> requires a non-empty File attribute.");
            string bindingsPath = ResolveReferencePath(configurationPath, file);
            if (!File.Exists(bindingsPath))
                throw new FileNotFoundException(
                    $"The machine bindings file '{bindingsPath}' does not exist.", bindingsPath);
            return Load(bindingsPath, expectedLibraryId);
        }

        public static LibraryMachineBindings Load(string path, Guid expectedLibraryId)
        {
            string fullPath = Path.GetFullPath(path);
            XElement root = XDocument.Load(fullPath).Element("LibraryBindings") ??
                throw new InvalidDataException(
                    "Missing <LibraryBindings> root element in the machine bindings file.");

            string? schemaText = Clean((string?)root.Attribute("SchemaVersion"));
            if (!int.TryParse(schemaText, out int schemaVersion) ||
                schemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported LibraryBindings SchemaVersion '{schemaText ?? "(missing)"}'.");

            string? libraryText = Clean((string?)root.Attribute("LibraryId"));
            if (!Guid.TryParse(libraryText, out Guid libraryId) || libraryId == Guid.Empty)
                throw new InvalidDataException(
                    "LibraryId on <LibraryBindings> must be a non-empty GUID.");
            if (libraryId != expectedLibraryId)
                throw new InvalidDataException(
                    $"Machine bindings belong to library '{libraryId:D}', but the portable " +
                    $"configuration identifies library '{expectedLibraryId:D}'.");

            string directory = Path.GetDirectoryName(fullPath)!;
            var rootPaths = new Dictionary<Guid, string>();
            foreach (XElement element in root.Elements("RootBinding"))
            {
                string? idText = Clean((string?)element.Attribute("RootId"));
                if (!Guid.TryParse(idText, out Guid rootId) || rootId == Guid.Empty)
                    throw new InvalidDataException(
                        "RootId on <RootBinding> must be a non-empty GUID.");
                string? value = Clean((string?)element.Attribute("Path"));
                if (value is null)
                    throw new InvalidDataException(
                        $"RootBinding '{rootId:D}' requires a non-empty Path attribute.");
                if (!rootPaths.TryAdd(rootId, ResolveFileSystemPath(value, directory)))
                    throw new InvalidDataException(
                        $"Machine bindings contain duplicate RootBinding '{rootId:D}'.");
            }

            ValidateToolBindings(root);
            string? database = SinglePath(root, "DatabaseBinding", "Path");
            string? ffmpeg = SingleToolPath(root, "Ffmpeg");
            string? wavpack = SingleToolPath(root, "Wavpack");
            string? monkeysAudio =
                SingleToolPath(root, "MonkeysAudio");
            string? itunes = SingleToolPath(root, "ItunesLibrary");
            var exportTransports = new Dictionary<string, LibraryExportTransportBinding>(
                StringComparer.OrdinalIgnoreCase);
            foreach (XElement element in root.Elements("ExportBinding"))
            {
                string? profileId = Clean((string?)element.Attribute("ProfileId"));
                if (profileId is null)
                    throw new InvalidDataException(
                        "Each <ExportBinding> requires a non-empty ProfileId attribute.");
                string? destination = Clean((string?)element.Attribute("Destination"));
                if (destination is null)
                    throw new InvalidDataException(
                        $"ExportBinding '{profileId}' requires a non-empty Destination attribute.");
                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XElement option in element.Elements("Option"))
                {
                    string? name = Clean((string?)option.Attribute("Name"));
                    if (name is null || !options.TryAdd(name,
                            (string?)option.Attribute("Value") ?? ""))
                        throw new InvalidDataException(
                            $"ExportBinding '{profileId}' contains an invalid or duplicate option.");
                }
                if (!exportTransports.TryAdd(profileId, new(profileId,
                        ResolveFileSystemPath(destination, directory), options)))
                    throw new InvalidDataException(
                        $"Machine bindings contain duplicate ExportBinding '{profileId}'.");
            }
            return new LibraryMachineBindings(
                fullPath,
                libraryId,
                rootPaths,
                database is null ? null : ResolveDatabase(database, directory),
                ffmpeg is null ? null : ResolveExecutable(ffmpeg, directory),
                wavpack is null ? null : ResolveExecutable(wavpack, directory),
                monkeysAudio is null
                    ? null
                    : ResolveExecutable(monkeysAudio, directory),
                itunes is null ? null : ResolveFileSystemPath(itunes, directory),
                exportTransports);
        }

        public void ValidateRootReferences(IEnumerable<Guid> configuredRootIds)
        {
            var configured = configuredRootIds.ToHashSet();
            Guid[] unknown = rootPaths_.Keys.Where(id => !configured.Contains(id)).ToArray();
            if (unknown.Length > 0)
                throw new InvalidDataException(
                    "Machine bindings reference unknown library root ID(s): " +
                    string.Join(", ", unknown.Select(id => id.ToString("D"))));
        }

        public void ValidateExportReferences(IEnumerable<string> configuredProfileIds)
        {
            var configured = configuredProfileIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] unknown = exportTransports_.Keys.Where(id => !configured.Contains(id))
                .ToArray();
            if (unknown.Length > 0)
                throw new InvalidDataException(
                    "Machine bindings reference unknown export profile ID(s): " +
                    string.Join(", ", unknown));
        }

        public static string ResolveReferencePath(string configurationPath, string reference)
        {
            string configurationDirectory = Path.GetDirectoryName(
                Path.GetFullPath(configurationPath))!;
            return Path.GetFullPath(reference, configurationDirectory);
        }

        private static string? SinglePath(XElement root, string elementName, string attributeName)
        {
            XElement[] elements = root.Elements(elementName).ToArray();
            if (elements.Length > 1)
                throw new InvalidDataException(
                    $"Only one <{elementName}> may be configured in machine bindings.");
            if (elements.Length == 0)
                return null;
            return Clean((string?)elements[0].Attribute(attributeName)) ??
                throw new InvalidDataException(
                    $"<{elementName}> requires a non-empty {attributeName} attribute.");
        }

        private static string? SingleToolPath(XElement root, string name)
        {
            XElement[] tools = root.Elements("ToolBinding")
                .Where(element => string.Equals(
                    Clean((string?)element.Attribute("Name")), name,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tools.Length > 1)
                throw new InvalidDataException(
                    $"Only one ToolBinding named '{name}' may be configured.");
            if (tools.Length == 0)
                return null;
            return Clean((string?)tools[0].Attribute("Path")) ??
                throw new InvalidDataException(
                    $"ToolBinding '{name}' requires a non-empty Path attribute.");
        }

        private static void ValidateToolBindings(XElement root)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement tool in root.Elements("ToolBinding"))
            {
                string? name = Clean((string?)tool.Attribute("Name"));
                if (name is null)
                    throw new InvalidDataException(
                        "Each <ToolBinding> requires a non-empty Name attribute.");
                if (Clean((string?)tool.Attribute("Path")) is null)
                    throw new InvalidDataException(
                        $"ToolBinding '{name}' requires a non-empty Path attribute.");
                if (!seen.Add(name))
                    throw new InvalidDataException(
                        $"Machine bindings contain duplicate ToolBinding '{name}'.");
            }
        }

        private static string ResolveFileSystemPath(string value, string directory) =>
            Path.GetFullPath(value, directory);

        private static string ResolveExecutable(string value, string directory) =>
            Path.IsPathRooted(value) ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar)
                ? Path.GetFullPath(value, directory)
                : value;

        private static string ResolveDatabase(string value, string directory)
        {
            if (value.StartsWith("sql:", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.StartsWith("sqlite:", StringComparison.OrdinalIgnoreCase))
            {
                string databasePath = value["sqlite:".Length..];
                if (string.IsNullOrWhiteSpace(databasePath))
                    throw new InvalidDataException(
                        "A sqlite: database binding must include a path.");
                return "sqlite:" + Path.GetFullPath(databasePath, directory);
            }
            return Path.GetFullPath(value, directory);
        }

        private static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

using System.Buffers.Binary;
using System.Text;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryInlineEditingRealServiceTests
{
    [Fact]
    public async Task Inline_edit_uses_the_real_transaction_path_and_converges_with_targeted_cache_refresh()
    {
        using var media = TestMediaFixture.Create();
        FixtureContext context = CreateContext(media);
        await context.ViewModel.ReloadAsync();
        LibraryRow row = Assert.Single(context.ViewModel.Rows);

        row.Title = "Reviewed transaction title";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        Assert.True(
            context.ViewModel
                .ApplyLibraryOperationCommand
                .CanExecute(null));
        Assert.Empty(context.History.Entries);
        Assert.Equal(
            "Original fixture title",
            ReadTitle(media.MediaPath));

        await context.ViewModel
            .ApplyLibraryOperationCommand
            .ExecuteAsync(null)
            .WaitAsync(
                TimeSpan.FromSeconds(15),
                TestContext.Current
                    .CancellationToken);

        Assert.Equal(
            "Reviewed transaction title",
            ReadTitle(media.MediaPath));
        Assert.Equal(
            "Reviewed transaction title",
            row.Title);
        Assert.Equal(
            "Reviewed transaction title",
            row.Record.Title);
        Assert.False(row.HasChanges);
        Assert.False(
            context.ViewModel.HasPendingChanges);

        EditHistoryEntry history =
            Assert.Single(context.History.Entries);
        string journal =
            Assert.Single(history.JournalPaths);
        Assert.True(
            File.Exists(journal),
            $"Expected a durable transaction journal at '{journal}'.");

        await context
            .TargetedRefresh
            .Completed
            .Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current
                    .CancellationToken);
        Assert.Equal(
            [media.MediaPath],
            context.TargetedRefresh.Paths);
        Assert.Equal(
            0,
            context.Library.IndexCallCount);

        await context.ViewModel.ReloadAsync();

        LibraryRow refreshed =
            Assert.Single(context.ViewModel.Rows);
        Assert.Equal(
            "Reviewed transaction title",
            refreshed.Title);
        Assert.Equal(
            "Reviewed transaction title",
            refreshed.Record.Title);
        Assert.False(refreshed.HasChanges);
        Assert.False(
            context.ViewModel.HasPendingChanges);
        Assert.Equal(
            0,
            context.Library.IndexCallCount);
    }

    [Fact]
    public async Task External_change_after_inline_capture_blocks_apply_and_retains_the_draft()
    {
        using var media = TestMediaFixture.Create();
        FixtureContext context = CreateContext(media);
        await context.ViewModel.ReloadAsync();
        LibraryRow row = Assert.Single(context.ViewModel.Rows);

        row.Title = "Initial unapplied reviewed title";
        WriteTitle(
            media.MediaPath,
            "Externally changed title with a different size");
        // The first edit starts a debounced preview. Under a heavily loaded
        // test runner that preview can finish before this test thread performs
        // the external write. A second edit invalidates that generation while
        // retaining the original edit-time source snapshot, guaranteeing that
        // the awaited preview was created after the external mutation.
        row.Title = "Unapplied reviewed title";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);

        Assert.True(
            context.ViewModel
                .IsDirectPendingPreviewReady);
        Assert.False(
            context.ViewModel
                .ApplyLibraryOperationCommand
                .CanExecute(null));
        Assert.True(row.HasChanges);
        Assert.Equal(
            "Unapplied reviewed title",
            row.Title);
        Assert.Equal(
            "Original fixture title",
            row.Record.Title);
        Assert.Equal(
            "Externally changed title with a different size",
            ReadTitle(media.MediaPath));
        Assert.Contains(
            context.ViewModel.PendingChanges,
            change =>
                change.HasDiagnosticDetail);
        Assert.Empty(context.History.Entries);
        Assert.Empty(
            context.TargetedRefresh.Paths);
        Assert.Equal(
            0,
            context.Library.IndexCallCount);
    }

    [Fact]
    public async Task Selected_inspector_refreshes_from_the_applied_file()
    {
        using var media = TestMediaFixture.Create();
        FixtureContext context = CreateContext(media);
        await context.ViewModel.ReloadAsync();
        LibraryRow row =
            Assert.Single(context.ViewModel.Rows);
        Assert.True(
            await context.ViewModel.SelectAsync(
                [row]));
        EditableTagField title =
            Assert.Single(
                context.Inspector.Fields,
                field =>
                    field.Field ==
                    TagFields.Title);
        Assert.Equal(
            "Original fixture title",
            title.Value);

        row.Title =
            "Inspector refreshed title";
        await WaitForAuthoritativePreviewAsync(
            context.ViewModel);
        await context.ViewModel
            .ApplyLibraryOperationCommand
            .ExecuteAsync(null)
            .WaitAsync(
                TimeSpan.FromSeconds(15),
                TestContext.Current
                    .CancellationToken);

        Assert.Equal(
            "Inspector refreshed title",
            title.Value);
        Assert.False(
            context.Inspector
                .HasUnsavedChanges);
        Assert.Equal(
            [media.MediaPath],
            context.Inspector
                .Selection.Paths);
    }

    private static FixtureContext CreateContext(
        TestMediaFixture media)
    {
        var settings =
            new AppSettings(media.SettingsPath);
        var records =
            new List<TrackRecord>
            {
                CreateRecord(
                    media.MediaPath),
            };
        var library =
            new FakeLibrary(records);
        var targetedRefresh =
            new RecordingTargetedReindex(
                records);
        var documents =
            new MetadataDocumentService(
                MediaFormatRegistry.Default);
        var history =
            new RecordingEditHistoryService();
        var operations =
            new MetadataOperationService(
                documents,
                MediaFormatRegistry.Default,
                new FileMutationPlanExecutor(
                    settings: settings),
                settings,
                targetedRefresh,
                history);
        var thumbnails =
            new FakeThumbnails();
        var inspector =
            new SelectionInspectorViewModel(
                new MediaFileService(
                    library,
                    MediaFormatRegistry.Default),
                library,
                new FakeTagWriter(),
                new FakeArtworkService(),
                new FakeFilePicker(),
                new FakeDialogs(),
                new FakeFieldsEditor(),
                thumbnails,
                new AppActivityService(),
                operations,
                documents);
        var indexing =
            new IndexingViewModel(
                library,
                settings,
                new AppActivityService());
        var viewModel =
            new LibraryViewModel(
                library,
                targetedRefresh,
                settings,
                inspector,
                new NavigationService(),
                indexing,
                thumbnails,
                metadataOperations:
                    operations,
                operationCatalog:
                    new MetadataOperationCatalog(),
                history: history);
        return new(
            viewModel,
            library,
            targetedRefresh,
            history,
            inspector);
    }

    private static async Task
        WaitForAuthoritativePreviewAsync(
            LibraryViewModel viewModel)
    {
        using var timeout =
            CancellationTokenSource
                .CreateLinkedTokenSource(
                    TestContext.Current
                        .CancellationToken);
        timeout.CancelAfter(
            TimeSpan.FromSeconds(10));
        while (!viewModel
                   .IsDirectPendingPreviewReady)
            await Task.Delay(
                20,
                timeout.Token);
    }

    private static TrackRecord CreateRecord(
        string path,
        IMediaFile? parsed = null)
    {
        parsed ??=
            MediaFile.GetFile(
                path,
                readOnly: true,
                readArtwork: false);
        KeyValuePair<TagFields, string>[] known =
        [
            .. parsed.Tags.SelectMany(
                tag => tag.GetKnownMetadata()),
        ];
        string? First(TagFields field) =>
            known.FirstOrDefault(
                value => value.Key == field)
                .Value;
        int? Number(TagFields field) =>
            int.TryParse(
                First(field),
                out int value)
                ? value
                : null;
        ICodecProvider? codec =
            parsed.Codecs.FirstOrDefault();
        var info = new FileInfo(path);
        info.Refresh();
        return new TrackRecord
        {
            Path = path,
            Title = First(TagFields.Title),
            Artist = First(TagFields.Artist),
            AlbumArtist =
                First(TagFields.AlbumArtist),
            HasAlbumArtist =
                !string.IsNullOrWhiteSpace(
                    First(TagFields.AlbumArtist)),
            Album = First(TagFields.Album),
            ReleaseDate =
                First(TagFields.Date),
            Genre = First(TagFields.Genre),
            Composer =
                First(TagFields.Composer),
            Grouping =
                First(TagFields.Grouping),
            TrackNumber =
                Number(TagFields.TrackNumber),
            TrackTotal =
                Number(TagFields.TotalTracks),
            DiscNumber =
                Number(TagFields.DiscNumber),
            DiscTotal =
                Number(TagFields.TotalDiscs),
            CodecName = codec?.CodecName,
            TagType =
                parsed.Tags.FirstOrDefault()
                    ?.TagType,
            CodecType =
                codec?.CodecType ??
                CodecType.Lossless,
            SampleRate =
                codec?.Samplerate ?? 0,
            BitsPerSample =
                codec?.BitsPerSample ?? 0,
            AverageBitRate =
                codec?.AverageBitrate ?? 0,
            Channels =
                codec?.Channels ?? 0,
            DurationInSeconds =
                checked((int)(
                    codec?.DurationInSeconds ??
                    0)),
            Length = info.Length,
            LastWriteTime =
                info.LastWriteTimeUtc,
            Metadata = known
                .GroupBy(value => value.Key)
                .ToDictionary(
                    group =>
                        group.Key.ToString(),
                    group =>
                        group.Select(
                                value =>
                                    value.Value)
                            .ToArray(),
                    StringComparer
                        .OrdinalIgnoreCase),
        };
    }

    private static string? ReadTitle(
        string path) =>
        MediaFile.GetFile(
                path,
                readOnly: true,
                readArtwork: false)
            .Tags.FirstOrDefault()
            ?.Title;

    private static void WriteTitle(
        string path,
        string title)
    {
        IMediaFile media =
            MediaFile.GetFile(
                path,
                readOnly: false,
                readArtwork: false);
        Assert.IsAssignableFrom<
                IMetadataWriter>(media)
            .SetField(
                TagFields.Title,
                title);
        media.SaveTags();
    }

    private sealed record FixtureContext(
        LibraryViewModel ViewModel,
        FakeLibrary Library,
        RecordingTargetedReindex
            TargetedRefresh,
        RecordingEditHistoryService
            History,
        SelectionInspectorViewModel
            Inspector);

    private sealed class
        RecordingTargetedReindex(
            List<TrackRecord> records) :
        IReindexService
    {
        public List<string> Paths
            { get; } =
            [];
        public TaskCompletionSource<bool>
            Completed { get; } =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        public Task<bool>
            IsIndexedFileAsync(
                string path,
                CancellationToken ct =
                    default) =>
            Task.FromResult(
                records.Any(
                    record =>
                        PathComparer.Equals(
                            record.Path,
                            path)));

        public Task ReindexFileAsync(
            string path,
            CancellationToken ct =
                default)
        {
            ct.ThrowIfCancellationRequested();
            return ReindexFileAsync(
                path,
                MediaFile.GetFile(
                    path,
                    readOnly: true,
                    readArtwork: false),
                ct);
        }

        public Task ReindexFileAsync(
            string path,
            IMediaFile savedFile,
            CancellationToken ct =
                default)
        {
            ct.ThrowIfCancellationRequested();
            int index =
                records.FindIndex(
                    record =>
                        PathComparer.Equals(
                            record.Path,
                            path));
            TrackRecord refreshed =
                CreateRecord(
                    path,
                    savedFile);
            if (index < 0)
                records.Add(refreshed);
            else
                records[index] = refreshed;
            Paths.Add(path);
            Completed
                .TrySetResult(true);
            return Task.CompletedTask;
        }

        private static StringComparer
            PathComparer =>
            OperatingSystem.IsWindows()
                ? StringComparer
                    .OrdinalIgnoreCase
                : StringComparer.Ordinal;
    }

    private sealed class
        RecordingEditHistoryService :
        IEditHistoryService
    {
        private readonly List<
            EditHistoryEntry> _entries =
            [];

        public IReadOnlyList<
            EditHistoryEntry> Entries =>
            _entries;
        public IReadOnlyList<
            EditHistoryEntry> RedoEntries =>
            [];
        public bool CanUndo =>
            _entries.Count > 0;
        public bool CanRedo => false;

        public void Record(
            EditHistoryEntry entry) =>
            _entries.Add(entry);

        public Task<int> UndoLatestAsync(
            IProgress<int>? progress =
                null,
            CancellationToken ct =
                default) =>
            throw new NotSupportedException();
    }

    private sealed class
        TestMediaFixture : IDisposable
    {
        private TestMediaFixture(
            string directory)
        {
            DirectoryPath = directory;
            MediaPath =
                Path.Combine(
                    directory,
                    "inline-edit.wav");
            SettingsPath =
                Path.Combine(
                    directory,
                    "settings.json");
            RecoveryPath =
                directory +
                ".MusicLibraryManager-recovery";
        }

        private string DirectoryPath
            { get; }
        public string MediaPath { get; }
        public string SettingsPath
            { get; }
        private string RecoveryPath
            { get; }

        public static TestMediaFixture
            Create()
        {
            string directory =
                Path.Combine(
                    Path.GetTempPath(),
                    "mlm-library-inline-real-" +
                    Guid.NewGuid()
                        .ToString("N"));
            Directory.CreateDirectory(
                directory);
            var fixture =
                new TestMediaFixture(
                    directory);
            WriteWaveFixture(
                fixture.MediaPath,
                "Original fixture title");
            return fixture;
        }

        public void Dispose()
        {
            TryDeleteDirectory(
                DirectoryPath);
            TryDeleteDirectory(
                RecoveryPath);
        }

        private static void
            TryDeleteDirectory(
                string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(
                        path,
                        recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void WriteWaveFixture(
        string path,
        string title)
    {
        using var stream =
            new MemoryStream();
        stream.Write("RIFF"u8);
        WriteUInt32(
            stream,
            0);
        stream.Write("WAVE"u8);
        WriteChunk(
            stream,
            "fmt ",
            WaveFormat());
        WriteChunk(
            stream,
            "data",
            [
                0x01, 0x02,
                0x03, 0x04,
                0x05, 0x06,
                0x07, 0x08,
            ]);
        WriteChunk(
            stream,
            "id3 ",
            BuildId3(title));

        byte[] file =
            stream.ToArray();
        BinaryPrimitives
            .WriteUInt32LittleEndian(
                file.AsSpan(4),
                checked(
                    (uint)file.Length -
                    8));
        File.WriteAllBytes(
            path,
            file);
    }

    private static byte[]
        WaveFormat()
    {
        byte[] data = new byte[16];
        BinaryPrimitives
            .WriteUInt16LittleEndian(
                data,
                1);
        BinaryPrimitives
            .WriteUInt16LittleEndian(
                data.AsSpan(2),
                2);
        BinaryPrimitives
            .WriteUInt32LittleEndian(
                data.AsSpan(4),
                44100);
        BinaryPrimitives
            .WriteUInt32LittleEndian(
                data.AsSpan(8),
                176400);
        BinaryPrimitives
            .WriteUInt16LittleEndian(
                data.AsSpan(12),
                4);
        BinaryPrimitives
            .WriteUInt16LittleEndian(
                data.AsSpan(14),
                16);
        return data;
    }

    private static byte[] BuildId3(
        string title)
    {
        byte[] value =
            Encoding.Latin1
                .GetBytes(title);
        byte[] frame =
            new byte[11 + value.Length];
        Encoding.ASCII
            .GetBytes("TIT2")
            .CopyTo(frame, 0);
        BinaryPrimitives
            .WriteUInt32BigEndian(
                frame.AsSpan(4),
                checked(
                    (uint)value.Length +
                    1));
        frame[10] = 0;
        value.CopyTo(
            frame,
            11);

        byte[] tag =
            new byte[10 + frame.Length];
        Encoding.ASCII
            .GetBytes("ID3")
            .CopyTo(tag, 0);
        tag[3] = 3;
        int size = frame.Length;
        tag[6] =
            (byte)((size >> 21) &
                   0x7F);
        tag[7] =
            (byte)((size >> 14) &
                   0x7F);
        tag[8] =
            (byte)((size >> 7) &
                   0x7F);
        tag[9] =
            (byte)(size &
                   0x7F);
        frame.CopyTo(tag, 10);
        return tag;
    }

    private static void WriteChunk(
        Stream stream,
        string id,
        byte[] data)
    {
        stream.Write(
            Encoding.ASCII
                .GetBytes(id));
        WriteUInt32(
            stream,
            checked(
                (uint)data.Length));
        stream.Write(data);
        if ((data.Length & 1) != 0)
            stream.WriteByte(0);
    }

    private static void WriteUInt32(
        Stream stream,
        uint value)
    {
        Span<byte> bytes =
            stackalloc byte[4];
        BinaryPrimitives
            .WriteUInt32LittleEndian(
                bytes,
                value);
        stream.Write(bytes);
    }
}

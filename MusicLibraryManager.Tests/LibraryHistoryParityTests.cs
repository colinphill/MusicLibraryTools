using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryHistoryParityTests
{
    [Fact]
    public async Task LibraryRepeatRedoAndUndoUseSharedHistory()
    {
        const string selectedPath =
            @"C:\music\selected.flac";
        const string redoPath =
            @"C:\music\redo.flac";
        TrackRecord[] records =
        [
            new()
            {
                Path = selectedPath,
                Artist = "Artist",
                AlbumArtist = "Artist",
                Album = "Album",
                Title = "Selected",
                CodecName = "FLAC",
                CodecType = CodecType.Lossless,
                DurationInSeconds = 120,
                LastWriteTime =
                    new DateTime(2026, 1, 1),
            },
        ];
        var library =
            new FakeLibrary(records);
        var settings =
            new FakeSettings();
        var activity =
            new AppActivityService();
        var inspector =
            new SelectionInspectorViewModel(
                new FakeMediaService(),
                library,
                new FakeTagWriter(),
                new FakeArtworkService(),
                new FakeFilePicker(),
                new FakeDialogs(),
                new FakeFieldsEditor(),
                new FakeThumbnails(),
                activity);
        var operations =
            new FakeMetadataOperationService();
        OperationRecipe recipe =
            OperationRecipe.Create(
                "Recent cleanup",
                new AssignFieldOperation(
                    MetadataFieldKey.Known(
                        TagFields.Title),
                    "Reviewed"));
        var history =
            new RecordingHistory(
                new(
                    Guid.NewGuid(),
                    recipe.Name,
                    DateTimeOffset.UtcNow,
                    [],
                    [selectedPath],
                    recipe),
                new(
                    Guid.NewGuid(),
                    recipe.Name,
                    DateTimeOffset.UtcNow,
                    [],
                    [redoPath],
                    recipe));
        var reindex = new FakeReindex();
        var viewModel =
            new LibraryViewModel(
                library,
                reindex,
                settings,
                inspector,
                new NavigationService(),
                new IndexingViewModel(
                    library,
                    settings,
                    activity),
                new FakeThumbnails(),
                metadataOperations: operations,
                operationCatalog:
                    new MetadataOperationCatalog(),
                dialogs: new FakeDialogs(),
                history: history);
        await viewModel.ReloadAsync();
        await viewModel.SelectAsync(
            [Assert.Single(viewModel.Rows)]);

        await viewModel.RepeatLibraryRecipeCommand
            .ExecuteAsync(null);

        Assert.Equal(
            [selectedPath],
            operations.PreviewedPaths);
        Assert.Contains(
            "current Library scope",
            viewModel.OperationStatus);

        await viewModel.RedoLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(
            [redoPath],
            operations.PreviewedPaths);
        Assert.Contains(
            "regenerated",
            viewModel.OperationStatus);

        await viewModel.UndoLibraryOperationCommand
            .ExecuteAsync(null);

        Assert.Equal(
            1,
            history.UndoCalls);
        Assert.Equal(
            [selectedPath],
            reindex.Paths);
        Assert.Contains(
            "Restored 1 file",
            viewModel.OperationStatus);
    }

    private sealed class RecordingHistory(
        EditHistoryEntry entry,
        EditHistoryEntry redo) :
        IEditHistoryService
    {
        private readonly List<EditHistoryEntry>
            _entries = [entry];
        private readonly List<EditHistoryEntry>
            _redo = [redo];

        public IReadOnlyList<EditHistoryEntry>
            Entries => _entries;

        public IReadOnlyList<EditHistoryEntry>
            RedoEntries => _redo;

        public bool CanUndo =>
            _entries.Count > 0;

        public bool CanRedo =>
            _redo.Count > 0;

        public int UndoCalls { get; private set; }

        public void Record(
            EditHistoryEntry historyEntry) =>
            _entries.Insert(0, historyEntry);

        public Task<int> UndoLatestAsync(
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            UndoCalls++;
            progress?.Report(1);
            EditHistoryEntry restored =
                _entries[0];
            _entries.RemoveAt(0);
            _redo.Insert(0, restored);
            return Task.FromResult(1);
        }
    }
}

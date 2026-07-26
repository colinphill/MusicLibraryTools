using System.Globalization;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class LibraryActionConsistencyTests
{
    [Fact]
    public async Task Selection_actions_retain_exact_ordered_paths_and_share_capabilities()
    {
        string first = Path.GetFullPath("library-first.flac");
        string second = Path.GetFullPath("library-second.flac");
        TrackRecord[] records =
        [
            new() { Path = first, Title = "First" },
            new() { Path = second, Title = "Second" },
        ];
        var reindex = new FakeReindex();
        var platform = new FakePlatformService();
        LibraryViewModel viewModel = BuildLibrary(
            records,
            reindex,
            platform);
        await viewModel.ReloadAsync();
        var mutableSelection = new List<string>
        {
            first,
            second,
            first,
        };

        LibraryActionScopeSnapshot multi =
            viewModel.CaptureActionScope(
                WorkbenchHandoffScopeKind.Selected,
                mutableSelection);
        mutableSelection.Clear();

        Assert.Equal(
            WorkbenchHandoffScopeKind.Selected,
            multi.ScopeKind);
        Assert.Equal([first, second], multi.CapturedPaths);
        Assert.True(multi.HasPaths);
        Assert.True(multi.CanCopyPaths);
        Assert.False(multi.CanReveal);
        Assert.True(multi.CanRefreshAffectedPaths);
        Assert.False(
            multi.CanHandoff,
            "The unit fixture intentionally has no Workbench host.");

        await viewModel.CopyPathsAsync(multi);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                first,
                second),
            platform.Text);

        await viewModel.RefreshAffectedPathsAsync(multi);
        Assert.Equal([first, second], reindex.Paths);

        LibraryActionScopeSnapshot single =
            viewModel.CaptureActionScope(
                WorkbenchHandoffScopeKind.Selected,
                [second]);
        Assert.True(single.CanReveal);
        viewModel.RevealPath(single);
        Assert.Equal(second, platform.RevealedPath);
    }

    [Fact]
    public async Task Visible_and_complete_scopes_are_immutable_and_busy_state_is_consistent()
    {
        string first = Path.GetFullPath("visible-first.flac");
        string second = Path.GetFullPath("visible-second.flac");
        TrackRecord[] records =
        [
            new() { Path = first, Title = "First" },
            new() { Path = second, Title = "Second" },
        ];
        LibraryViewModel viewModel = BuildLibrary(
            records,
            new FakeReindex(),
            new FakePlatformService());
        await viewModel.ReloadAsync();
        viewModel.FilterText = "First";
        await viewModel.ApplyFilterNowAsync(
            TestContext.Current.CancellationToken);

        LibraryActionScopeSnapshot visible =
            viewModel.CaptureActionScope(
                WorkbenchHandoffScopeKind.VisibleResults);
        LibraryActionScopeSnapshot complete =
            viewModel.CaptureActionScope(
                WorkbenchHandoffScopeKind.AllResults);
        viewModel.Rows = [];

        Assert.Equal([first], visible.CapturedPaths);
        Assert.Equal([first, second], complete.CapturedPaths);

        viewModel.IsBusy = true;
        LibraryActionScopeSnapshot busy =
            viewModel.CaptureActionScope(
                WorkbenchHandoffScopeKind.Selected,
                [first, second]);

        Assert.True(busy.CanCopyPaths);
        Assert.False(busy.CanReveal);
        Assert.False(busy.CanRefreshAffectedPaths);
        Assert.False(busy.CanHandoff);
    }

    [Fact]
    public void Library_custom_column_choices_refresh_labels_without_changing_values_or_order()
    {
        var localization =
            new SwitchingLocalizationService();
        var editor =
            new MetadataGridColumnEditorViewModel(
                store: null,
                MetadataGridSurface.Library,
                localization);
        LocalizedChoice<MetadataGridFieldKind>[] fieldChoices =
            [.. editor.FieldKindChoices];
        LocalizedChoice<MetadataGridColumnSortType>[] sortChoices =
            [.. editor.SortTypeChoices];
        editor.FieldKind = MetadataGridFieldKind.Custom;
        editor.SortType = MetadataGridColumnSortType.Numeric;

        Assert.Equal(
            [
                MetadataGridFieldKind.Known,
                MetadataGridFieldKind.Custom,
            ],
            fieldChoices.Select(choice => choice.Value));
        Assert.Equal(
            [
                MetadataGridColumnSortType.Text,
                MetadataGridColumnSortType.Numeric,
                MetadataGridColumnSortType.Date,
            ],
            sortChoices.Select(choice => choice.Value));
        Assert.All(
            fieldChoices.Concat<object>(sortChoices),
            choice => Assert.Contains(
                "en-US:",
                choice.ToString(),
                StringComparison.Ordinal));

        localization.SetCulture("de-DE");

        Assert.Equal(fieldChoices, editor.FieldKindChoices);
        Assert.Equal(sortChoices, editor.SortTypeChoices);
        Assert.Equal(
            [
                MetadataGridFieldKind.Known,
                MetadataGridFieldKind.Custom,
            ],
            editor.FieldKindChoices.Select(
                choice => choice.Value));
        Assert.Equal(
            [
                MetadataGridColumnSortType.Text,
                MetadataGridColumnSortType.Numeric,
                MetadataGridColumnSortType.Date,
            ],
            editor.SortTypeChoices.Select(
                choice => choice.Value));
        Assert.All(
            editor.FieldKindChoices
                .Cast<object>()
                .Concat(editor.SortTypeChoices),
            choice => Assert.Contains(
                "de-DE:",
                choice.ToString(),
                StringComparison.Ordinal));
        Assert.Equal(
            MetadataGridFieldKind.Custom,
            editor.FieldKind);
        Assert.Equal(
            MetadataGridColumnSortType.Numeric,
            editor.SortType);
    }

    private static LibraryViewModel BuildLibrary(
        IReadOnlyList<TrackRecord> records,
        IReindexService reindex,
        IPlatformService platform)
    {
        var library = new FakeLibrary(records);
        var settings = new FakeSettings();
        var activity = new AppActivityService();
        var inspector = new SelectionInspectorViewModel(
            new FakeMediaService(),
            library,
            new FakeTagWriter(),
            new FakeArtworkService(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            activity);
        var indexing = new IndexingViewModel(
            library,
            settings,
            activity);
        return new(
            library,
            reindex,
            settings,
            inspector,
            new NavigationService(),
            indexing,
            new FakeThumbnails(),
            platform: platform);
    }

    private sealed class SwitchingLocalizationService :
        ILocalizationService
    {
        private CultureInfo _culture =
            CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentUICulture => _culture;
        public IReadOnlyList<CultureInfo> SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("de-DE"),
        ];
        public event EventHandler? CultureChanged;

        public string Get(string key) =>
            $"{_culture.Name}:{key}";

        public string Format(
            string key,
            params object?[] arguments) =>
            Get(key);

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            Get(
                $"{key}.{(count == 1 ? "One" : "Other")}");

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(string cultureName)
        {
            _culture =
                CultureInfo.GetCultureInfo(
                    cultureName);
            CultureChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
    }
}

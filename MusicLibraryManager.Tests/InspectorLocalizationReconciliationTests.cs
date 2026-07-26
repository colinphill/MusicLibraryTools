using System.Globalization;
using System.Text.RegularExpressions;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class
    InspectorLocalizationReconciliationTests
{
    [Fact]
    public async Task
        Artwork_sharing_is_a_complete_separate_sentence_and_relocalizes_live()
    {
        string[] paths =
        [
            @"C:\Music\one.flac",
            @"C:\Music\two.flac",
        ];
        var library = new FakeLibrary([]);
        library.ImageSignatures[paths[0]] = "same-artwork";
        library.ImageSignatures[paths[1]] = "same-artwork";
        var localization =
            new InspectorTestLocalizationService();
        var inspector = CreateInspector(
            paths.Select(path => ModelWithArtwork(path))
                .ToArray(),
            library,
            localization);

        await inspector.LoadAsync(
            new SelectionContext(paths));

        Assert.Equal(
            "Shared by 2 tracks.",
            inspector.ArtworkSharingSummary);
        Assert.True(
            inspector.HasArtworkSharingSummary);
        Assert.DoesNotContain(
            inspector.ArtworkSharingSummary,
            inspector.ArtworkSummary,
            StringComparison.Ordinal);

        localization.SetCulture("ja-JP");

        Assert.Equal(
            "2 曲でアートワークを共有しています。",
            inspector.ArtworkSharingSummary);
        Assert.True(
            inspector.HasArtworkSharingSummary);

        await inspector.LoadAsync(
            new SelectionContext([paths[0]]));

        Assert.Empty(
            inspector.ArtworkSharingSummary);
        Assert.False(
            inspector.HasArtworkSharingSummary);

        const string noArtworkPath =
            @"C:\Music\no-artwork.flac";
        await inspector.LoadAsync(
            new SelectionContext(
                [noArtworkPath]));

        Assert.Empty(
            inspector.ArtworkSharingSummary);
        Assert.False(
            inspector.HasArtworkSharingSummary);
    }

    [Fact]
    public async Task
        Discard_and_revert_use_complete_non_English_questions_without_fragment_arguments()
    {
        const string path =
            @"C:\Music\one.flac";
        var localization =
            new InspectorTestLocalizationService();
        var dialogs =
            new CapturingInspectorDialogs(
                result: false);
        var inspector = CreateInspector(
            [new MediaFileModel { Path = path }],
            new FakeLibrary([]),
            localization,
            dialogs: dialogs);
        await inspector.LoadAsync(
            new SelectionContext([path]));
        inspector.Fields.Single(field =>
                field.Field == TagFields.Title)
            .Value = "Edited";
        localization.SetCulture("ja-JP");

        bool discarded =
            await inspector
                .ConfirmDiscardChangesAsync();
        await inspector.RevertCommand
            .ExecuteAsync(null);

        Assert.False(discarded);
        Assert.Collection(
            dialogs.Calls,
            discard => Assert.Equal(
                "現在の選択項目の未保存の変更を破棄して続行しますか？",
                discard.Message),
            revert => Assert.Equal(
                "未保存の変更を、選択したファイルに現在保存されている値に戻しますか？",
                revert.Message));
        Assert.DoesNotContain(
            "Inspector.Dialog.Discard.Message",
            localization.FormattedKeys);
        Assert.DoesNotContain(
            "Inspector.Dialog.Revert.Message",
            localization.FormattedKeys);
    }

    [Fact]
    public async Task
        Artwork_picker_title_and_APIC_filename_token_do_not_depend_on_the_localized_type_label()
    {
        const string path =
            @"C:\Music\one.flac";
        var localization =
            new InspectorTestLocalizationService();
        localization.SetCulture("ja-JP");
        var files =
            new CapturingInspectorFilePicker();
        var inspector = CreateInspector(
            [new MediaFileModel { Path = path }],
            new FakeLibrary([]),
            localization,
            files: files);
        await inspector.LoadAsync(
            new SelectionContext([path]));
        var item = new ArtworkPreviewItem(
            null,
            ID3v2Util.APICType.BackCover,
            "image/png",
            [1, 2, 3],
            "PNG",
            null);
        item.RefreshLocalizedText(_ =>
            "背面カバー");
        inspector.ArtworkItems.Add(item);

        await inspector
            .SaveArtworkItemToFileAsync(item);

        Assert.Equal(
            "アートワークを保存",
            files.SaveTitle);
        Assert.Equal(
            "one-back-cover.png",
            files.SuggestedName);
        Assert.Equal(
            ".png",
            files.Extension);
        Assert.DoesNotContain(
            "Inspector.Picker.SaveArtwork",
            localization.FormattedKeys);
    }

    [Fact]
    public void APIC_filename_tokens_match_the_complete_invariant_contract()
    {
        (
            ID3v2Util.APICType Type,
            string Token)[] expected =
        [
            (ID3v2Util.APICType.Other,
                "other"),
            (ID3v2Util.APICType.FileIcon,
                "file-icon"),
            (ID3v2Util.APICType.OtherFileIcon,
                "other-file-icon"),
            (ID3v2Util.APICType.FrontCover,
                "front-cover"),
            (ID3v2Util.APICType.BackCover,
                "back-cover"),
            (ID3v2Util.APICType.LeafletPage,
                "leaflet-page"),
            (ID3v2Util.APICType.Media,
                "media"),
            (ID3v2Util.APICType.LeadArtist,
                "lead-artist"),
            (ID3v2Util.APICType.Arist,
                "arist"),
            (ID3v2Util.APICType.Conductor,
                "conductor"),
            (ID3v2Util.APICType.Band,
                "band"),
            (ID3v2Util.APICType.Composer,
                "composer"),
            (ID3v2Util.APICType.Lyricist,
                "lyricist"),
            (ID3v2Util.APICType.RecordingLocation,
                "recording-location"),
            (ID3v2Util.APICType.DuringRecording,
                "during-recording"),
            (ID3v2Util.APICType.DuringPerformance,
                "during-performance"),
            (ID3v2Util.APICType.VideoScreenCapture,
                "video-screen-capture"),
            (ID3v2Util.APICType.BrightColoredFish,
                "bright-colored-fish"),
            (ID3v2Util.APICType.Illustration,
                "illustration"),
            (ID3v2Util.APICType.BandLogo,
                "band-logo"),
            (ID3v2Util.APICType.StudioLogo,
                "studio-logo"),
        ];

        Assert.Equal(
            Enum.GetValues<
                ID3v2Util.APICType>(),
            expected.Select(pair =>
                pair.Type));
        Assert.Equal(
            expected.Select(pair =>
                pair.Token),
            expected.Select(pair =>
                SelectionInspectorViewModel
                    .InvariantArtworkTypeFileToken(
                        pair.Type)));
        Assert.Equal(
            expected.Length,
            expected.Select(pair =>
                    pair.Token)
                .Distinct(
                    StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void Inspector_source_keeps_localized_sentences_and_filename_tokens_semantically_separate()
    {
        string source = File.ReadAllText(
            FindRepositoryFile(
                Path.Combine(
                    "MusicLibraryManager.Presentation",
                    "SelectionInspectorViewModel.cs")));
        string view = File.ReadAllText(
            FindRepositoryFile(
                Path.Combine(
                    "MusicLibraryManager",
                    "Views",
                    "SelectionInspectorView.axaml")));

        Assert.DoesNotMatch(
            new Regex(
                """
                LF\s*\(\s*"Inspector\.(?:Dialog\.(?:Discard|Revert)\.Message|Picker\.SaveArtwork)"
                """,
                RegexOptions.CultureInvariant),
            source);
        Assert.DoesNotContain(
            "ArtworkSummary +=",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "item.Label.ToLowerInvariant()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "InvariantArtworkTypeFileToken(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding ArtworkSharingSummary}\"",
            view,
            StringComparison.Ordinal);
    }

    private static SelectionInspectorViewModel
        CreateInspector(
            MediaFileModel[] models,
            FakeLibrary library,
            ILocalizationService localization,
            IFilePickerService? files = null,
            IDialogCoordinator? dialogs = null) =>
        new(
            new FakeMediaService(models),
            library,
            new FakeTagWriter(),
            new FakeArtworkService(),
            files ??
            new CapturingInspectorFilePicker(),
            dialogs ??
            new CapturingInspectorDialogs(),
            new FakeFieldsEditor(),
            new FakeThumbnails(),
            new AppActivityService(),
            localization: localization);

    private static MediaFileModel
        ModelWithArtwork(string path) =>
        new()
        {
            Path = path,
            Artwork =
            [
                new ArtworkModel
                {
                    Category = "FrontCover",
                    ImageType = "image/jpeg",
                    Width = 800,
                    Height = 800,
                    Size = 3,
                    Data = [1, 2, 3],
                },
            ],
        };

    private static string FindRepositoryFile(
        string relativePath)
    {
        DirectoryInfo? directory =
            new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate =
                Path.Combine(
                    directory.FullName,
                    relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}'.");
    }

    private sealed class
        InspectorTestLocalizationService :
        ILocalizationService
    {
        private CultureInfo _culture =
            CultureInfo.GetCultureInfo(
                "en-US");

        public List<string> FormattedKeys
            { get; } = [];
        public CultureInfo CurrentUICulture =>
            _culture;
        public IReadOnlyList<CultureInfo>
            SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo(
                "en-US"),
            CultureInfo.GetCultureInfo(
                "ja-JP"),
        ];
        public event EventHandler?
            CultureChanged;

        public string Get(string key) =>
            (_culture.Name, key) switch
            {
                (
                    "ja-JP",
                    "Inspector.Dialog.Discard.Message") =>
                    "現在の選択項目の未保存の変更を破棄して続行しますか？",
                (
                    "ja-JP",
                    "Inspector.Dialog.Revert.Message") =>
                    "未保存の変更を、選択したファイルに現在保存されている値に戻しますか？",
                (
                    "ja-JP",
                    "Inspector.Picker.SaveArtwork") =>
                    "アートワークを保存",
                (
                    "ja-JP",
                    "Inspector.Artwork.Type.FrontCover.Label") =>
                    "前面カバー",
                (
                    "ja-JP",
                    "Inspector.Artwork.Type.BackCover.Label") =>
                    "背面カバー",
                (
                    "en-US",
                    "Inspector.Dialog.Discard.Message") =>
                    "Discard the unsaved changes for the current selection and continue?",
                (
                    "en-US",
                    "Inspector.Dialog.Revert.Message") =>
                    "Revert the unsaved changes to the values currently stored in the selected files?",
                (
                    "en-US",
                    "Inspector.Picker.SaveArtwork") =>
                    "Save artwork",
                _ => $"{_culture.Name}:{key}",
            };

        public string Format(
            string key,
            params object?[] arguments)
        {
            FormattedKeys.Add(key);
            return string.Join(
                "|",
                Get(key),
                string.Join(
                    ",",
                    arguments));
        }

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments)
        {
            FormattedKeys.Add(key);
            if (key ==
                "Inspector.Artwork.Shared")
                return _culture.Name ==
                       "ja-JP"
                    ? $"{count:N0} 曲でアートワークを共有しています。"
                    : $"Shared by {count:N0} tracks.";
            return $"{_culture.Name}:{key}:{count:N0}";
        }

        public IReadOnlyDictionary<
            string,
            string> Snapshot() =>
            new Dictionary<string, string>();

        public void SetCulture(
            string cultureName)
        {
            _culture =
                CultureInfo.GetCultureInfo(
                    cultureName);
            CultureChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
    }

    private sealed class
        CapturingInspectorFilePicker :
        IFilePickerService
    {
        public string? SaveTitle
            { get; private set; }
        public string? SuggestedName
            { get; private set; }
        public string? Extension
            { get; private set; }

        public Task<string?> PickFileAsync(
            string title,
            IReadOnlyList<FilePickerType>?
                types = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(
            string title) =>
            Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(
            string title,
            string suggestedName,
            string extension)
        {
            SaveTitle = title;
            SuggestedName = suggestedName;
            Extension = extension;
            return Task.FromResult<
                string?>(null);
        }
    }

    private sealed class
        CapturingInspectorDialogs(
            bool result = true) :
        IDialogCoordinator
    {
        public List<(
            string Title,
            string Message,
            string PrimaryText)> Calls
            { get; } = [];

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string primaryText)
        {
            Calls.Add((
                title,
                message,
                primaryText));
            return Task.FromResult(result);
        }

        public Task ShowMessageAsync(
            string title,
            string message) =>
            Task.CompletedTask;
    }
}

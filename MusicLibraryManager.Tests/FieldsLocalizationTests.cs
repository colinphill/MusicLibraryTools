using System.Globalization;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class FieldsLocalizationTests
{
    [Fact]
    public void Fields_editor_runtime_text_uses_localization_and_diagnostic_binding()
    {
        string source = File.ReadAllText(
            FindRepositoryFile(
                Path.Combine(
                    "MusicLibraryManager.Presentation",
                    "Workflows",
                    "FieldsDialogViewModel.cs")));
        string view = File.ReadAllText(
            FindRepositoryFile(
                Path.Combine(
                    "MusicLibraryManager",
                    "Views",
                    "FieldsEditorView.axaml")));

        Assert.DoesNotMatch(
            @"StatusMessage\s*=\s*\$?""[^""]*[A-Za-z]",
            source);
        Assert.DoesNotMatch(
            @"(?:Kind|RemoveButtonText|SaveButtonText|CancelButtonText|Title)\s*=>\s*\$?""",
            source);
        Assert.DoesNotContain(
            "file(s)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "blocker(s)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "field change(s)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "update.Message",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AddableFieldChoices}\"",
            view,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding DiagnosticDetail}\"",
            view,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Culture_refresh_preserves_rows_choices_pending_edits_and_preview()
    {
        var localization =
            new SwitchingLocalizationService();
        var viewModel = new FieldsDialogViewModel(
            new FakeMetadataDocumentService(
                Document(
                    @"C:\music\track.flac")),
            new FakeMetadataOperationService(),
            [@"C:\music\track.flac"],
            (_, _) => Task.FromResult(true),
            localization: localization);
        await viewModel.Loading;

        FieldRow title = viewModel.Rows.Single(
            row => row.Field == TagFields.Title);
        FieldRow userString = viewModel.Rows.Single(
            row => row.UserStringKey == "Catalog Code");
        title.Value = "Edited title";
        viewModel.FieldToAdd = TagFields.Album;
        LocalizedChoice<TagFields>[] choices =
            [.. viewModel.AddableFieldChoices];
        TagFields[] values = choices
            .Select(choice => choice.Value)
            .ToArray();
        string[] labels = choices
            .Select(choice => choice.Label)
            .ToArray();

        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(
            viewModel.IsConfirmingSave,
            viewModel.StatusMessage);
        string? previewStatus =
            viewModel.StatusMessage;

        localization.SetCulture("fr-FR");

        Assert.Same(
            title,
            viewModel.Rows.Single(
                row => row.Field == TagFields.Title));
        Assert.Same(
            userString,
            viewModel.Rows.Single(
                row => row.UserStringKey ==
                    "Catalog Code"));
        Assert.Equal(
            "Edited title",
            title.Value);
        Assert.True(title.IsModified);
        Assert.True(viewModel.HasPendingChanges);
        Assert.True(viewModel.IsConfirmingSave);
        Assert.Equal(
            TagFields.Album,
            viewModel.FieldToAdd);
        Assert.Equal(
            "Catalog Code",
            userString.Name);
        Assert.StartsWith(
            "fr-FR:Settings.Choice.TagFields.Title",
            title.Name,
            StringComparison.Ordinal);
        Assert.Equal(
            values,
            viewModel.AddableFieldChoices.Select(
                choice => choice.Value));
        Assert.All(
            choices.Select(
                (choice, index) =>
                    (choice, index)),
            pair => Assert.Same(
                pair.choice,
                viewModel.AddableFieldChoices[
                    pair.index]));
        Assert.All(
            choices.Select(
                (choice, index) =>
                    (choice, index)),
            pair => Assert.NotEqual(
                labels[pair.index],
                pair.choice.Label));
        Assert.NotEqual(
            previewStatus,
            viewModel.StatusMessage);
        Assert.StartsWith(
            "fr-FR:Fields.Status.PreviewReady:1|1",
            viewModel.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_failure_counts_are_paired_and_raw_details_stay_separate()
    {
        var oneLocalization =
            new SwitchingLocalizationService();
        var one = new FieldsDialogViewModel(
            new ThrowingMetadataDocumentService(
                "decoder exploded"),
            new FakeMetadataOperationService(),
            [@"C:\music\one.flac"],
            (_, _) => Task.FromResult(true),
            localization: oneLocalization);
        await one.Loading;

        Assert.StartsWith(
            "en-US:Fields.Title.Single:",
            one.Title,
            StringComparison.Ordinal);
        Assert.Contains(
            "one.flac",
            one.Title,
            StringComparison.Ordinal);
        Assert.Equal(
            "en-US:Fields.Status.LoadFailures.One:1",
            one.StatusMessage);
        Assert.DoesNotContain(
            "decoder exploded",
            one.StatusMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"C:\music\one.flac",
            one.StatusMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            "decoder exploded",
            one.DiagnosticDetail,
            StringComparison.Ordinal);
        Assert.Contains(
            @"C:\music\one.flac",
            one.DiagnosticDetail,
            StringComparison.Ordinal);
        string? diagnostic =
            one.DiagnosticDetail;

        oneLocalization.SetCulture("fr-FR");

        Assert.Equal(
            "fr-FR:Fields.Status.LoadFailures.One:1",
            one.StatusMessage);
        Assert.Equal(
            diagnostic,
            one.DiagnosticDetail);

        var many = new FieldsDialogViewModel(
            new ThrowingMetadataDocumentService(
                "decoder exploded"),
            new FakeMetadataOperationService(),
            [
                @"C:\music\one.flac",
                @"C:\music\two.flac",
            ],
            (_, _) => Task.FromResult(true),
            localization:
                new SwitchingLocalizationService());
        await many.Loading;

        Assert.Equal(
            "en-US:Fields.Title.Files.Other:2",
            many.Title);
        Assert.Equal(
            "en-US:Fields.Status.LoadFailures.Other:2",
            many.StatusMessage);
    }

    private static MediaDocument Document(
        string path) =>
        new(
            path,
            [new(
                "VorbisComment",
                [
                    new(
                        MetadataFieldKey.Known(
                            TagFields.Title),
                        ["Original title"]),
                    new(
                        MetadataFieldKey.Custom(
                            "Catalog Code"),
                        ["ABC-123"]),
                ],
                true,
                true,
                true,
                true)],
            [],
            null,
            new(
                path,
                10,
                DateTime.UtcNow,
                "hash"),
            true);

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

    private sealed class ThrowingMetadataDocumentService(
        string message) :
        IMetadataDocumentService
    {
        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromException<MediaDocument>(
                new InvalidOperationException(
                    message));
        }
    }

    private sealed class SwitchingLocalizationService :
        ILocalizationService
    {
        private CultureInfo _culture =
            CultureInfo.GetCultureInfo("en-US");

        public CultureInfo CurrentUICulture =>
            _culture;
        public IReadOnlyList<CultureInfo>
            SupportedCultures { get; } =
        [
            CultureInfo.GetCultureInfo("en-US"),
            CultureInfo.GetCultureInfo("fr-FR"),
        ];
        public event EventHandler? CultureChanged;

        public string Get(string key) =>
            $"{_culture.Name}:{key}";

        public string Format(
            string key,
            params object?[] arguments) =>
            $"{Get(key)}:{string.Join(
                "|",
                arguments)}";

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{Get(
                $"{key}.{(
                    count == 1
                        ? "One"
                        : "Other")}")}:{count}";

        public IReadOnlyDictionary<string, string>
            Snapshot() =>
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
}

using System.Collections.ObjectModel;
using System.Globalization;
using iTunes.Binary;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class RemainingPresentationLocalizationTests
{
    [Fact]
    public void Audio_artwork_and_mapping_statuses_localize_while_diagnostics_remain_raw()
    {
        var localization =
            new SwitchingLocalizationService();
        const string audioDiagnostic =
            "decoder exploded at byte 42";
        const string artworkDiagnostic =
            "HTTP 503 from artwork host";
        const string musicBrainzDiagnostic =
            "two MusicBrainz tracks tied at score 90";
        const string discogsDiagnostic =
            "Discogs position could not be resolved";
        const string path =
            @"C:\Music\User Album\Track.flac";
        var discovery = new AcoustIdDiscoveryResult(
        [
            new(
                path,
                null,
                null,
                [new(
                    "decoder",
                    OperationIssueSeverity.Warning,
                    audioDiagnostic,
                    path)]),
        ]);
        AudioDiscoveryRow audio =
            Assert.Single(
                AudioDiscoveryRows.Create(
                    discovery,
                    localization));
        var candidate =
            new CoverArtArchiveCandidate(
                Guid.NewGuid(),
                "cover-1",
                new Uri(
                    "https://example.test/cover.jpg"),
                null,
                [],
                IsFront: true,
                IsBack: false,
                Approved: false,
                Comment: "User cover note");
        var artwork =
            new CoverArtCandidateRow(
                candidate,
                localization);
        artwork.SetThumbnailStatus(
            "Workbench.Online.Thumbnail.Failed");
        artwork.ThumbnailDiagnosticDetail =
            artworkDiagnostic;
        var musicBrainzMatch =
            new MusicBrainzTrackMatch(
                new(
                    path,
                    []),
                null,
                [],
                MusicBrainzMappingConfidence.Ambiguous,
                musicBrainzDiagnostic);
        var musicBrainz =
            new MusicBrainzTrackMappingRow(
                musicBrainzMatch,
                localization);
        var discogsMatch =
            new DiscogsTrackMatch(
                new(
                    path,
                    Title: "User title"),
                null,
                [],
                DiscogsMappingConfidence.Unmatched,
                discogsDiagnostic);
        var discogs =
            new DiscogsTrackMappingRow(
                discogsMatch,
                localization);
        string audioStatus = audio.Status;
        string artworkStatus =
            artwork.ThumbnailStatus!;
        string artworkRole = artwork.Roles;
        string musicBrainzStatus =
            musicBrainz.Status;
        string musicBrainzConfidence =
            musicBrainz.Confidence;
        string discogsStatus =
            discogs.Status;
        string discogsConfidence =
            discogs.Confidence;

        localization.SetCulture("fr-FR");
        audio.RefreshLocalizedText();
        artwork.RefreshLocalizedText();
        musicBrainz.RefreshLocalizedText();
        discogs.RefreshLocalizedText();

        Assert.NotEqual(
            audioStatus,
            audio.Status);
        Assert.StartsWith(
            "fr-FR:OnlineMetadata.AudioDiscovery.Status.NoMatch",
            audio.Status,
            StringComparison.Ordinal);
        Assert.Equal(
            audioDiagnostic,
            audio.DiagnosticDetail);
        Assert.Equal(
            path,
            audio.Path);
        Assert.NotEqual(
            artworkStatus,
            artwork.ThumbnailStatus);
        Assert.StartsWith(
            "fr-FR:Workbench.Online.Thumbnail.Failed",
            artwork.ThumbnailStatus,
            StringComparison.Ordinal);
        Assert.NotEqual(
            artworkRole,
            artwork.Roles);
        Assert.StartsWith(
            "fr-FR:OnlineMetadata.CoverArt.Role.Other",
            artwork.Roles,
            StringComparison.Ordinal);
        Assert.Equal(
            artworkDiagnostic,
            artwork.ThumbnailDiagnosticDetail);
        Assert.Same(
            candidate,
            artwork.Candidate);
        Assert.Equal(
            "User cover note",
            artwork.Comment);
        Assert.NotEqual(
            musicBrainzStatus,
            musicBrainz.Status);
        Assert.NotEqual(
            musicBrainzConfidence,
            musicBrainz.Confidence);
        Assert.StartsWith(
            "fr-FR:OnlineMetadata.MusicBrainz.MappingStatus.Ambiguous",
            musicBrainz.Status,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "fr-FR:OnlineMetadata.Mapping.Confidence.Ambiguous",
            musicBrainz.Confidence,
            StringComparison.Ordinal);
        Assert.Equal(
            musicBrainzDiagnostic,
            musicBrainz.DiagnosticDetail);
        Assert.Null(
            musicBrainz.SelectedTrack);
        Assert.False(
            musicBrainz.IsIncluded);
        Assert.NotEqual(
            discogsStatus,
            discogs.Status);
        Assert.NotEqual(
            discogsConfidence,
            discogs.Confidence);
        Assert.StartsWith(
            "fr-FR:OnlineMetadata.Discogs.MappingStatus.Unmatched",
            discogs.Status,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "fr-FR:OnlineMetadata.Mapping.Confidence.Unmatched",
            discogs.Confidence,
            StringComparison.Ordinal);
        Assert.Equal(
            discogsDiagnostic,
            discogs.DiagnosticDetail);
        Assert.Equal(
            path,
            discogs.Path);
    }

    [Fact]
    public void Metadata_editor_preserves_choices_selection_and_preview_semantics_across_culture()
    {
        var localization =
            new SwitchingLocalizationService();
        var editor =
            new MetadataOperationEditorViewModel(
                new MetadataOperationCatalog(),
                MetadataOperationSurface.Workbench,
                localization: localization);
        MetadataOperationDescriptor assign =
            editor.OperationDescriptors.Single(
                descriptor =>
                    descriptor.Kind ==
                    MetadataOperationKind.Assign);
        LocalizedChoice<
            MetadataOperationDescriptor>
            assignChoice =
                editor.OperationChoices.Single(
                    choice =>
                        choice.Value.Kind ==
                        MetadataOperationKind.Assign);
        LocalizedChoice<MetadataCaseMode>
            caseChoice =
                editor.CaseModeChoices.Single(
                    choice =>
                        choice.Value ==
                        MetadataCaseMode.Title);
        LocalizedChoice<MetadataValueOrder>
            orderChoice =
                editor.ValueOrderChoices.Single(
                    choice =>
                        choice.Value ==
                        MetadataValueOrder.Ascending);
        LocalizedChoice<MetadataPathComponent>
            pathChoice =
                editor.PathComponentChoices.Single(
                    choice =>
                        choice.Value ==
                        MetadataPathComponent
                            .FileNameWithoutExtension);
        LocalizedChoice<MetadataConditionOperator>
            conditionChoice =
                editor.ConditionOperatorChoices.Single(
                    choice =>
                        choice.Value ==
                        MetadataConditionOperator.Present);
        MetadataFieldChoice album =
            editor.Fields.Single(
                field =>
                    field.Field ==
                    TagFields.Album);
        editor.SelectedOperation = assign;
        editor.SelectedField = album;
        editor.OperationValue =
            "User-authored value";
        editor.RecipeName =
            "User-authored recipe";
        editor.AddCurrentOperationCommand
            .Execute(null);
        MetadataRecipeStepViewModel step =
            Assert.Single(
                editor.Steps);
        string stepName = step.Name;
        string assignLabel =
            assignChoice.Label;
        string caseLabel =
            caseChoice.Label;
        string orderLabel =
            orderChoice.Label;
        string pathLabel =
            pathChoice.Label;
        string conditionLabel =
            conditionChoice.Label;
        string fieldLabel =
            album.Label;
        OperationRecipe recipe =
            editor.CreateRecipe(
                editor.RecipeName);
        const string filePath =
            @"C:\Music\User Album\Track.flac";
        var field =
            MetadataFieldKey.Known(
                TagFields.Album);
        var plan =
            new MetadataOperationPlan(
                Guid.NewGuid(),
                "User plan",
                [new(
                    filePath,
                    new(
                        filePath,
                        10,
                        DateTime.UtcNow,
                        "hash"),
                    [new(
                        field,
                        ["Raw before value"],
                        ["User-authored value"])],
                    [new(
                        field,
                        ["User-authored value"])],
                    [])],
                DateTimeOffset.UtcNow,
                recipe);
        var pending =
            new ObservableCollection<
                PendingMetadataOperationRow>();
        var preview =
            new ObservableCollection<
                MetadataPreviewRow>();
        PendingMetadataOperationRowBuilder
            .Populate(
                pending,
                plan,
                localization);
        MetadataPreviewRowBuilder.Populate(
            preview,
            plan,
            localization);
        PendingMetadataOperationRow
            pendingEnglish =
                Assert.Single(
                    pending);
        MetadataPreviewRow previewEnglish =
            Assert.Single(
                preview);

        localization.SetCulture("fr-FR");

        Assert.Same(
            assign,
            editor.SelectedOperation);
        Assert.Same(
            album,
            editor.SelectedField);
        Assert.Equal(
            "User-authored value",
            editor.OperationValue);
        Assert.Equal(
            "User-authored recipe",
            editor.RecipeName);
        Assert.Same(
            step,
            Assert.Single(
                editor.Steps));
        Assert.Same(
            step.Operation,
            Assert.Single(
                editor.Steps).Operation);
        Assert.Equal(
            stepName,
            step.Name);
        Assert.Same(
            assignChoice,
            editor.OperationChoices.Single(
                choice =>
                    choice.Value.Kind ==
                    MetadataOperationKind.Assign));
        Assert.Same(
            caseChoice,
            editor.CaseModeChoices.Single(
                choice =>
                    choice.Value ==
                    MetadataCaseMode.Title));
        Assert.Same(
            orderChoice,
            editor.ValueOrderChoices.Single(
                choice =>
                    choice.Value ==
                    MetadataValueOrder.Ascending));
        Assert.Same(
            pathChoice,
            editor.PathComponentChoices.Single(
                choice =>
                    choice.Value ==
                    MetadataPathComponent
                        .FileNameWithoutExtension));
        Assert.Same(
            conditionChoice,
            editor.ConditionOperatorChoices.Single(
                choice =>
                    choice.Value ==
                    MetadataConditionOperator.Present));
        Assert.NotEqual(
            assignLabel,
            assignChoice.Label);
        Assert.NotEqual(
            caseLabel,
            caseChoice.Label);
        Assert.NotEqual(
            orderLabel,
            orderChoice.Label);
        Assert.NotEqual(
            pathLabel,
            pathChoice.Label);
        Assert.NotEqual(
            conditionLabel,
            conditionChoice.Label);
        Assert.NotEqual(
            fieldLabel,
            album.Label);

        PendingMetadataOperationRowBuilder
            .Populate(
                pending,
                plan,
                localization);
        MetadataPreviewRowBuilder.Populate(
            preview,
            plan,
            localization);
        PendingMetadataOperationRow pendingFrench =
            Assert.Single(
                pending);
        MetadataPreviewRow previewFrench =
            Assert.Single(
                preview);

        Assert.Equal(
            pendingEnglish.Number,
            pendingFrench.Number);
        Assert.Equal(
            pendingEnglish.Operation,
            pendingFrench.Operation);
        Assert.NotEqual(
            pendingEnglish.Target,
            pendingFrench.Target);
        Assert.NotEqual(
            pendingEnglish.AppliesTo,
            pendingFrench.AppliesTo);
        Assert.Contains(
            "User-authored value",
            pendingFrench.Target,
            StringComparison.Ordinal);
        Assert.Equal(
            previewEnglish.File,
            previewFrench.File);
        Assert.Equal(
            "Raw before value",
            previewFrench.Before);
        Assert.Equal(
            "User-authored value",
            previewFrench.After);
        Assert.NotEqual(
            previewEnglish.Field,
            previewFrench.Field);
        Assert.Equal(
            filePath,
            Assert.Single(
                plan.Files).Path);
        Assert.Same(
            recipe,
            plan.Recipe);
    }

    [Fact]
    public void Analyzer_culture_refresh_localizes_text_differences_without_changing_repairs()
    {
        var localization =
            new SwitchingLocalizationService();
        string path =
            "C:\\Music\\User\u00A0Album\\Track.flac";
        string after =
            "\u00A0\u0301ABCDEFGHIJKLM";
        var repair =
            new AnalysisTagRepair(
                path,
                TagFields.Title,
                null,
                after,
                "Raw normalization rationale",
                10,
                DateTime.UtcNow);
        var repairItem =
            new AnalysisRepairItemViewModel(
                repair,
                localization)
            {
                Disposition =
                    AnalysisRepairDisposition.Active,
            };
        var repairRun =
            AnalysisRunViewModel.ForRepairs(
                new(
                    "Raw repair plan",
                    [repair]),
                [repairItem],
                [],
                "Raw repair summary",
                localization: localization);
        var itlDifference =
            new ItlMetadataDifference(
                "User field name",
                "Old\u00A0value",
                after);
        var itlRepair =
            new ItlMetadataRepairItem(
                Guid.NewGuid(),
                17,
                0x1020304050607080,
                path,
                new()
                {
                    Artist =
                        "User artist",
                    Album =
                        "User album",
                    Title =
                        "User title",
                },
                DateTime.UtcNow,
                [itlDifference]);
        var itlItem =
            new ItlMetadataRepairItemViewModel(
                itlRepair,
                localization)
            {
                Disposition =
                    AnalysisRepairDisposition.Active,
            };
        var itlPlan =
            new ItlMetadataRepairPlan(
                "User Library.itl",
                "RAW-HASH",
                DateTimeOffset.UtcNow,
                [itlRepair]);
        var itlRun =
            AnalysisRunViewModel.ForItlRepairs(
                itlPlan,
                [itlItem],
                "Raw ITL summary",
                localization: localization);
        var analyzer =
            new AnalyzerViewModel(
                new FakeLibrary([]),
                new StubArtistReconciler(),
                new StubAnalysisRepairService(),
                new FakeSettings(),
                localization: localization);
        analyzer.Runs.Add(
            repairRun);
        analyzer.Runs.Add(
            itlRun);
        string repairBefore =
            repairItem.Before;
        string repairDisplayPath =
            repairItem.DisplayPath;
        string repairUnicode =
            repairItem.UnicodeDifferenceDetails!;
        string itlBefore =
            itlItem.Before;
        string itlAfter =
            itlItem.After;
        string itlDisplayPath =
            itlItem.DisplayPath;

        Assert.Contains(
            "en-US:Health.TextDifference.Missing",
            string.Concat(
                repairItem.BeforeDifference.Select(
                    segment =>
                        segment.Text)),
            StringComparison.Ordinal);
        Assert.Contains(
            "en-US:Health.TextDifference.NoBreakSpaceMarker",
            string.Concat(
                repairItem.AfterDifference.Select(
                    segment =>
                        segment.Text)),
            StringComparison.Ordinal);
        Assert.Contains(
            "Health.TextDifference.UnicodeName.NoBreakSpace",
            repairUnicode,
            StringComparison.Ordinal);
        Assert.Contains(
            "Health.TextDifference.UnicodeCategory.NonSpacingMark",
            repairUnicode,
            StringComparison.Ordinal);
        Assert.Contains(
            "Health.TextDifference.UnicodeCategory.UppercaseLetter",
            repairUnicode,
            StringComparison.Ordinal);
        Assert.Contains(
            "Health.TextDifference.More.Other",
            repairUnicode,
            StringComparison.Ordinal);

        try
        {
            localization.SetCulture(
                "fr-FR");

            Assert.Same(
                repair,
                repairItem.Repair);
            Assert.Same(
                repairItem,
                Assert.Single(
                    repairRun.RepairItems));
            Assert.Equal(
                AnalysisRepairDisposition.Active,
                repairItem.Disposition);
            Assert.Equal(
                "Raw normalization rationale",
                repairItem.Reason);
            Assert.NotEqual(
                repairBefore,
                repairItem.Before);
            Assert.StartsWith(
                "fr-FR:Health.Common.Missing",
                repairItem.Before,
                StringComparison.Ordinal);
            Assert.NotEqual(
                repairDisplayPath,
                repairItem.DisplayPath);
            Assert.Contains(
                "fr-FR:Health.TextDifference.NoBreakSpaceMarker",
                repairItem.DisplayPath,
                StringComparison.Ordinal);
            Assert.NotEqual(
                repairUnicode,
                repairItem.UnicodeDifferenceDetails);
            Assert.Contains(
                "fr-FR:Health.TextDifference.More.Other",
                repairItem.UnicodeDifferenceDetails,
                StringComparison.Ordinal);
            Assert.Same(
                itlRepair,
                itlItem.Item);
            Assert.Same(
                itlItem,
                Assert.Single(
                    itlRun.ItlRepairItems));
            Assert.Equal(
                17,
                itlItem.Item.TrackId);
            Assert.Equal(
                0x1020304050607080UL,
                itlItem.Item.PersistentId);
            Assert.Equal(
                AnalysisRepairDisposition.Active,
                itlItem.Disposition);
            Assert.Same(
                itlDifference,
                Assert.Single(
                    itlItem.Item.Differences));
            Assert.NotEqual(
                itlBefore,
                itlItem.Before);
            Assert.NotEqual(
                itlAfter,
                itlItem.After);
            Assert.NotEqual(
                itlDisplayPath,
                itlItem.DisplayPath);
            Assert.Contains(
                "fr-FR:Health.Itl.DifferenceFormat",
                itlItem.Before,
                StringComparison.Ordinal);
            Assert.Contains(
                "User field name",
                itlItem.After,
                StringComparison.Ordinal);
            Assert.Contains(
                "fr-FR:Health.TextDifference.NoBreakSpaceMarker",
                itlItem.DisplayPath,
                StringComparison.Ordinal);
            Assert.Equal(
                path,
                itlItem.Path);
        }
        finally
        {
            HealthLocalizedChoices.Refresh(
                LocalizedText.Get);
        }
    }

    private sealed class StubArtistReconciler :
        IArtistReconciler
    {
        public IReadOnlyList<SimilarArtistGroup>
            FindSimilarArtists(
            IReadOnlyList<TrackRecord> records,
            double threshold = 0.2,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> RenameArtistAsync(
            IReadOnlyList<string> paths,
            string from,
            string to,
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAnalysisRepairService :
        IAnalysisRepairService
    {
        public AnalysisRepairPlan PreviewSafeRepairs(
            IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();

        public AnalysisRepairPlan
            PreviewMissingAlbumArtists(
            IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();

        public AnalysisRepairPlan
            PreviewNumberingAndTotals(
            IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();

        public AnalysisRepairPlan
            PreviewTextNormalization(
            IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();

        public AnalysisRepairPlan
            PreviewMultiDiscAlbumNames(
            IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();

        public AnalysisRepairPlan
            PreviewId3VersionUpgrades(
            IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();

        public IReadOnlyList<AnalysisTagConflict>
            FindAlbumArtistConflicts(
            IReadOnlyList<TrackRecord> records) =>
            throw new NotSupportedException();

        public AnalysisRepairPlan
            PreviewConflictRepairs(
            IReadOnlyList<
                AnalysisConflictResolution>
                resolutions) =>
            throw new NotSupportedException();

        public Task<BatchWriteResult> ApplyAsync(
            AnalysisRepairPlan plan,
            IProgress<int>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
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
                arguments.Select(
                    argument =>
                        argument?.ToString() ??
                        "<null>"))}";

        public string FormatCount(
            string key,
            long count,
            params object?[] arguments) =>
            $"{Get(
                $"{key}.{(
                    count == 1
                        ? "One"
                        : "Other")}")}:{count}:" +
            string.Join(
                "|",
                arguments.Select(
                    argument =>
                        argument?.ToString() ??
                        "<null>"));

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

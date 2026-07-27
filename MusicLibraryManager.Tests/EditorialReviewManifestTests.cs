using System.Globalization;
using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class EditorialReviewManifestTests
{
    [Fact]
    public void Checked_in_manifest_is_digest_bound_and_reports_truthful_counts()
    {
        ReviewFixture fixture = LoadFixture();

        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);

        Assert.Equal(3_658, manifest.Records.Count);
        Assert.Equal(
            0,
            Count(
                manifest,
                EditorialReviewStatus.Pending));
        Assert.Equal(
            115,
            Count(
                manifest,
                EditorialReviewStatus.InvariantApproved));
        Assert.Equal(
            247,
            Count(
                manifest,
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            3_296,
            Count(
                manifest,
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            CatalogTranslationRoute.EditorialOverride,
            manifest.Records["Common.Beta"].Route);
        Assert.Equal(
            EditorialReviewStatus.EditorialReviewed,
            manifest.Records["Common.Beta"].Status);
        Assert.Equal(127, fixture.InvariantApprovedValues.Count);

        EditorialReviewRecord[] lazyMetadataRecords =
        [
            .. manifest.Records.Values
                .Where(record =>
                    record.Batch ==
                    "memory-audit-lazy-metadata-2026-07-27")
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal),
        ];
        Assert.Equal(
        [
            "Library.Metadata.ExactValueRequired",
            "Library.Metadata.Loading",
            "Library.Metadata.Unavailable",
        ], lazyMetadataRecords.Select(record => record.Key));
        Assert.All(
            lazyMetadataRecords,
            record =>
            {
                Assert.Equal(
                    EditorialReviewStatus.EditorialReviewed,
                    record.Status);
                Assert.Equal(
                    CatalogTranslationRoute.EditorialOverride,
                    record.Route);
                Assert.Equal(
                    "Codex memory audit localization review",
                    record.Reviewer);
                Assert.Equal("2026-07-27", record.Date);
            });
    }

    [Fact]
    public void Manifest_rejects_stale_digest_missing_key_and_duplicate_key()
    {
        ReviewFixture fixture = LoadFixture();
        CatalogReviewSource first = fixture.Sources[0];
        var changedTranslations =
            first.Translations.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);
        changedTranslations["de-DE"] += " changed";
        CatalogReviewSource[] staleSources =
        [
            first with
            {
                Translations = changedTranslations,
            },
            .. fixture.Sources.Skip(1),
        ];

        InvalidDataException stale = Assert.Throws<InvalidDataException>(
            () => EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                staleSources,
                fixture.InvariantApprovedValues,
                requireComplete: false));
        Assert.Contains(
            "digest is stale",
            stale.Message,
            StringComparison.Ordinal);

        WithTemporaryDirectory(directory =>
        {
            XDocument missing = XDocument.Load(fixture.ManifestPath);
            missing.Root!.Elements("entry").First().Remove();
            string path = Path.Combine(directory, "missing.xml");
            missing.Save(path);
            InvalidDataException exception =
                Assert.Throws<InvalidDataException>(
                    () =>
                        EditorialReviewInfrastructure.LoadAndValidate(
                            path,
                            fixture.Sources,
                            fixture.InvariantApprovedValues,
                            requireComplete: false));
            Assert.Contains(
                "missing resources",
                exception.Message,
                StringComparison.Ordinal);
        });

        WithTemporaryDirectory(directory =>
        {
            XDocument duplicate = XDocument.Load(fixture.ManifestPath);
            duplicate.Root!.Add(
                new XElement(
                    duplicate.Root.Elements("entry").First()));
            string path = Path.Combine(directory, "duplicate.xml");
            duplicate.Save(path);
            InvalidDataException exception =
                Assert.Throws<InvalidDataException>(
                    () =>
                        EditorialReviewInfrastructure.LoadAndValidate(
                            path,
                            fixture.Sources,
                            fixture.InvariantApprovedValues,
                            requireComplete: false));
            Assert.Contains(
                "is duplicated",
                exception.Message,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Invariant_approval_is_bound_to_the_exact_approved_value()
    {
        ReviewFixture fixture = LoadFixture();
        CatalogReviewSource original =
            fixture.Sources.First(source =>
                fixture.InvariantApprovedValues.ContainsKey(
                    source.Key));
        string revisedValue =
            original.Neutral + " independently revised";
        CatalogReviewSource revised = original with
        {
            Neutral = revisedValue,
            Translations =
                EditorialReviewInfrastructure.ShippingCultures
                    .ToDictionary(
                        culture => culture,
                        _ => revisedValue,
                        StringComparer.Ordinal),
        };
        var staleApproval = new Dictionary<string, string>(
            StringComparer.Ordinal)
        {
            [revised.Key] =
                fixture.InvariantApprovedValues[revised.Key],
        };

        WithTemporaryDirectory(directory =>
        {
            InvalidDataException stale =
                Assert.Throws<InvalidDataException>(
                    () => EditorialReviewInfrastructure.Refresh(
                        Path.Combine(directory, "stale.xml"),
                        [revised],
                        new Dictionary<
                            string,
                            ReviewedCatalogEvidence>(
                            StringComparer.Ordinal),
                        staleApproval,
                        "test",
                        "Test reviewer",
                        "2026-07-25"));
            Assert.Contains(
                "approved value digests changed",
                stale.Message,
                StringComparison.Ordinal);

            EditorialReviewManifest pending =
                EditorialReviewInfrastructure.Refresh(
                    Path.Combine(directory, "pending.xml"),
                    [revised],
                    new Dictionary<
                        string,
                        ReviewedCatalogEvidence>(
                        StringComparer.Ordinal),
                    new Dictionary<string, string>(
                        StringComparer.Ordinal),
                    "test",
                    "Test reviewer",
                    "2026-07-25");
            Assert.Equal(
                EditorialReviewStatus.Pending,
                pending.Records[revised.Key].Status);

            var revisedApproval = new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [revised.Key] =
                    EditorialReviewInfrastructure
                        .ComputeInvariantValueDigest(revised),
            };
            EditorialReviewManifest approved =
                EditorialReviewInfrastructure.Refresh(
                    Path.Combine(directory, "approved.xml"),
                    [revised],
                    new Dictionary<
                        string,
                        ReviewedCatalogEvidence>(
                        StringComparer.Ordinal),
                    revisedApproval,
                    "test",
                    "Test reviewer",
                    "2026-07-25");
            Assert.Equal(
                EditorialReviewStatus.InvariantApproved,
                approved.Records[revised.Key].Status);
        });
    }

    [Fact]
    public void Manifest_digest_binds_every_record_and_provenance_field()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        EditorialReviewRecord original =
            manifest.Records.Values.First(record =>
                record.Status ==
                    EditorialReviewStatus.EditorialReviewed);
        string originalDigest = ReadManifestDigest(
            EditorialReviewInfrastructure.SerializeManifest(
                manifest));
        EditorialReviewRecord[] mutations =
        [
            original with { Key = original.Key + ".Changed" },
            original with
            {
                Digest = FlipDigest(original.Digest),
            },
            original with
            {
                Status = EditorialReviewStatus.GlossaryReviewed,
            },
            original with
            {
                Route = CatalogTranslationRoute.Glossary,
            },
            original with { Batch = original.Batch + "-changed" },
            original with
            {
                Reviewer = original.Reviewer + " changed",
            },
            original with { Date = "2026-07-26" },
            original with
            {
                Disposition = original.Disposition + ":changed",
            },
        ];

        foreach (EditorialReviewRecord mutation in mutations)
        {
            var records = manifest.Records.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);
            records[original.Key] = mutation;
            string changedDigest = ReadManifestDigest(
                EditorialReviewInfrastructure.SerializeManifest(
                    new EditorialReviewManifest(records)));
            Assert.NotEqual(originalDigest, changedDigest);
        }
    }

    [Fact]
    public void Canonical_validation_rejects_invalid_combinations_even_with_a_recomputed_digest()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        EditorialReviewRecord reviewed =
            manifest.Records.Values.First(record =>
                record.Status ==
                    EditorialReviewStatus.EditorialReviewed);
        EditorialReviewRecord pending =
            reviewed with
            {
                Status = EditorialReviewStatus.Pending,
                Batch = "editorial-backlog-v1",
                Reviewer = "Unassigned",
                Disposition = "pending:v1",
            };
        EditorialReviewRecord[] invalidRecords =
        [
            reviewed with
            {
                Status = EditorialReviewStatus.GlossaryReviewed,
            },
            reviewed with
            {
                Disposition = "review-set:v1:not-a-digest",
            },
            pending with { Batch = "forged-review" },
        ];

        WithTemporaryDirectory(directory =>
        {
            for (int index = 0;
                 index < invalidRecords.Length;
                 index++)
            {
                EditorialReviewRecord invalid =
                    invalidRecords[index];
                var records = manifest.Records.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal);
                records[invalid.Key] = invalid;
                string path = Path.Combine(
                    directory,
                    $"invalid-{index}.xml");
                AtomicOutputBatch.Commit(
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        [path] =
                            EditorialReviewInfrastructure
                                .SerializeManifest(
                                    new EditorialReviewManifest(
                                        records)),
                    });

                InvalidDataException failure =
                    Assert.Throws<InvalidDataException>(
                        () => EditorialReviewInfrastructure
                            .LoadAndValidate(
                                path,
                                fixture.Sources,
                                fixture.InvariantApprovedValues,
                                requireComplete: false));
                Assert.Contains(
                    "noncanonical",
                    failure.Message,
                    StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void Manifest_rejects_status_provenance_swap_and_refresh_cannot_launder_it()
    {
        ReviewFixture fixture = LoadFixture();
        XDocument checkedIn = XDocument.Load(
            fixture.ManifestPath);
        XElement reviewed = checkedIn.Root!
            .Elements("entry")
            .First(entry =>
                string.Equals(
                    (string?)entry.Attribute("status"),
                    EditorialReviewStatus
                        .EditorialReviewed.ToString(),
                    StringComparison.Ordinal));
        XElement glossary = checkedIn.Root!
            .Elements("entry")
            .First(entry =>
                string.Equals(
                    (string?)entry.Attribute("status"),
                    EditorialReviewStatus
                        .GlossaryReviewed.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)entry.Attribute("route"),
                    CatalogTranslationRoute.Glossary.ToString(),
                    StringComparison.Ordinal));
        string[] provenanceAttributes =
        [
            "status",
            "batch",
            "reviewer",
            "date",
            "disposition",
        ];
        foreach (string attributeName in provenanceAttributes)
        {
            string reviewedValue =
                (string)reviewed.Attribute(attributeName)!;
            reviewed.SetAttributeValue(
                attributeName,
                (string)glossary.Attribute(attributeName)!);
            glossary.SetAttributeValue(
                attributeName,
                reviewedValue);
        }

        WithTemporaryDirectory(directory =>
        {
            string path = Path.Combine(
                directory,
                "tampered.xml");
            checkedIn.Save(path);
            InvalidDataException loadFailure =
                Assert.Throws<InvalidDataException>(
                    () =>
                        EditorialReviewInfrastructure.LoadAndValidate(
                            path,
                            fixture.Sources,
                            fixture.InvariantApprovedValues,
                            requireComplete: false));
            Assert.True(
                loadFailure.Message.Contains(
                    "noncanonical",
                    StringComparison.Ordinal) ||
                loadFailure.Message.Contains(
                    "manifestDigest does not match",
                    StringComparison.Ordinal),
                loadFailure.Message);

            InvalidDataException refreshFailure =
                Assert.Throws<InvalidDataException>(
                    () => EditorialReviewInfrastructure.Refresh(
                        path,
                        fixture.Sources,
                        new Dictionary<
                            string,
                            ReviewedCatalogEvidence>(
                            StringComparer.Ordinal),
                        fixture.InvariantApprovedValues,
                        "test",
                        "Test reviewer",
                        "2026-07-25"));
            Assert.True(
                refreshFailure.Message.Contains(
                    "noncanonical",
                    StringComparison.Ordinal) ||
                refreshFailure.Message.Contains(
                    "manifestDigest does not match",
                    StringComparison.Ordinal),
                refreshFailure.Message);
        });
    }

    [Fact]
    public void Strict_gate_passes_when_every_resource_has_review_evidence()
    {
        ReviewFixture fixture = LoadFixture();

        EditorialReviewManifest complete =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: true);

        Assert.Equal(
            0,
            Count(
                complete,
                EditorialReviewStatus.Pending));
    }

    [Fact]
    public void Checked_in_evidence_is_a_valid_rebase_seed_while_manifest_preserves_reviewed_provenance()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest current =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        EditorialReviewSeed seed =
            EditorialReviewInfrastructure.LoadReviewEvidence(
                fixture.ReviewEvidencePath,
                fixture.Sources);
        Assert.Empty(seed.Catalogs);
        Assert.Equal(
            "gui-usability-final-copy-2026-07-26",
            seed.Batch);
        Assert.Equal(
            "Codex usability localization review",
            seed.Reviewer);
        Assert.Equal("2026-07-26", seed.Date);
        Assert.Equal(
            832,
            current.Records.Values.Count(record =>
                record.Disposition.StartsWith(
                    "review-set:v1:",
                    StringComparison.Ordinal)));
        Assert.Equal(
            45,
            current.Records.Values.Count(record =>
                record.Batch ==
                "gui-usability-final-copy-2026-07-26"));

        WithTemporaryDirectory(directory =>
        {
            string path = Path.Combine(directory, "manifest.xml");
            File.Copy(
                fixture.ManifestPath,
                path);
            EditorialReviewManifest refreshed =
                EditorialReviewInfrastructure.Refresh(
                    path,
                    fixture.Sources,
                    seed.Catalogs,
                    fixture.InvariantApprovedValues,
                    seed.Batch,
                    seed.Reviewer,
                    seed.Date);

            Assert.Equal(
                3_296,
                Count(
                    refreshed,
                    EditorialReviewStatus.EditorialReviewed));
            Assert.Equal(
                115,
                Count(
                    refreshed,
                    EditorialReviewStatus.InvariantApproved));
            Assert.Equal(
                0,
                Count(
                    refreshed,
                    EditorialReviewStatus.Pending));
            Assert.Equal(
                247,
                Count(
                    refreshed,
                    EditorialReviewStatus.GlossaryReviewed));
            Assert.Equal(
                File.ReadAllText(fixture.ManifestPath),
                EditorialReviewInfrastructure.SerializeManifest(
                    refreshed));
        });
    }

    [Fact]
    public void Final_copy_review_batch_contains_exactly_the_approved_resources()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-final-copy-2026-07-26";
        string[] expectedKeys =
        [
            "Health.Audit.Run",
            "Health.Repair.Prepare",
            "Library.PendingChanges.FieldChanged",
            "Library.PendingChanges.SourceChanged",
            "Operations.Action.Maintenance",
            "ReviewedFileOperation.EmptyPreviewDescription",
            "ReviewedFileOperation.EmptyPreviewTitle",
            "Settings.Appearance.DisplayLanguageDescription",
            "Workbench.Bulk.EmptyPreviewDescription",
            "Workbench.Bulk.EmptyPreviewTitle",
            "Workbench.Choice.ImportEmptyCellMode.RemoveField",
            "Workbench.Columns.InlineEditingDescription",
            "Workbench.Inspector.CopyPrimaryMetadataHelp",
            "Workbench.Inspector.Id3ConversionHelp",
            "Workbench.Inspector.Id3EncodingHelp",
            "Workbench.Inspector.LayerCopyHelp",
            "Workbench.Online.BuildMapping",
            "Workbench.Online.Empty.ArtworkCandidatesDescription",
            "Workbench.Online.Empty.ArtworkCandidatesTitle",
            "Workbench.Online.Empty.ArtworkDescription",
            "Workbench.Online.Empty.ArtworkTitle",
            "Workbench.Online.Empty.AwaitingDiscoveryDescription",
            "Workbench.Online.Empty.AwaitingSearchDescription",
            "Workbench.Online.Empty.AwaitingTitle",
            "Workbench.Online.Empty.MappingDescription",
            "Workbench.Online.Empty.MappingTitle",
            "Workbench.Online.Empty.NoResultsDescription",
            "Workbench.Online.Empty.NoResultsTitle",
            "Workbench.Online.ExportArtwork",
            "Workbench.Online.ExportArtworkAutomation",
            "Workbench.Online.PreviewCover",
            "Workbench.Online.RemoveArtwork",
            "Workbench.Online.RemoveArtworkAutomation",
            "Workbench.Online.ReplaceArtwork",
            "Workbench.Online.ReplaceArtworkAutomation",
            "Workbench.Playlists.EmptyPreviewDescription",
            "Workbench.Playlists.EmptyPreviewTitle",
            "Workbench.Reports.EmptyPreviewDescription",
            "Workbench.Reports.EmptyPreviewTitle",
            "Workbench.Session.EmptyCellAutomation",
            "Workbench.Session.EmptyCellTooltip",
            "Workbench.Tools.BrowseExecutable",
            "Workbench.Tools.BrowseFolder",
            "Workbench.Tools.EmptyPreviewDescription",
            "Workbench.Tools.EmptyPreviewTitle",
        ];
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    record.Batch == batch)
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(
            expectedKeys,
            records.Select(record =>
                record.Key));
        Assert.All(
            records,
            record =>
            {
                Assert.Equal(
                    EditorialReviewStatus.EditorialReviewed,
                    record.Status);
                Assert.Equal(
                    CatalogTranslationRoute.EditorialOverride,
                    record.Route);
                Assert.Equal(
                    "Codex usability localization review",
                    record.Reviewer);
                Assert.Equal("2026-07-26", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Small_workflow_review_batch_contains_exactly_the_approved_domains()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-small-workflow-editorial-2026-07-25";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(170, records.Length);
        Assert.Equal(
            162,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            8,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.GlossaryReviewed));
        Assert.All(
            records,
            record =>
            {
                Assert.Equal(
                    "Codex focused editorial review",
                    record.Reviewer);
                Assert.Equal(
                    "2026-07-25",
                    record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });

        Dictionary<string, int> expectedDomainCounts =
            new(StringComparer.Ordinal)
            {
                ["About"] = 5,
                ["Activity"] = 5,
                ["ArtworkPreview"] = 5,
                ["Common"] = 22,
                ["Count"] = 2,
                ["Dialog"] = 23,
                ["FieldsEditor"] = 5,
                ["Home"] = 24,
                ["Index"] = 19,
                ["OnlineMetadata"] = 21,
                ["Organize"] = 29,
                ["Technical"] = 2,
                ["Transcode"] = 8,
            };
        Dictionary<string, int> actualDomainCounts =
            records
                .GroupBy(
                    record =>
                        record.Key.Split('.')[0],
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.Ordinal);

        Assert.Equal(
            expectedDomainCounts.OrderBy(
                item => item.Key,
                StringComparer.Ordinal),
            actualDomainCounts.OrderBy(
                item => item.Key,
                StringComparer.Ordinal));
    }

    [Fact]
    public void Operations_review_batch_contains_the_exact_focused_domain()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-operations-editorial-2026-07-25";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(211, records.Length);
        Assert.Equal(
            176,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            35,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            176,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.EditorialOverride));
        Assert.Equal(
            34,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.Glossary));
        Assert.Single(
            records,
            record =>
                record.Route ==
                CatalogTranslationRoute.ExactResource);
        Assert.All(
            records,
            record =>
            {
                Assert.StartsWith(
                    "Operations.",
                    record.Key,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Codex focused editorial review",
                    record.Reviewer);
                Assert.Equal("2026-07-25", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Devices_review_batch_contains_the_exact_focused_domain()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-devices-editorial-2026-07-25";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(125, records.Length);
        Assert.All(
            records,
            record =>
            {
                Assert.StartsWith(
                    "Devices.",
                    record.Key,
                    StringComparison.Ordinal);
                Assert.Equal(
                    EditorialReviewStatus.EditorialReviewed,
                    record.Status);
                Assert.Equal(
                    CatalogTranslationRoute.EditorialOverride,
                    record.Route);
                Assert.Equal(
                    "Codex focused editorial review",
                    record.Reviewer);
                Assert.Equal("2026-07-25", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Ingest_review_batch_contains_the_exact_focused_domain()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-ingest-editorial-2026-07-25";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(123, records.Length);
        Assert.Equal(
            96,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            27,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            96,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.EditorialOverride));
        Assert.Equal(
            27,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.Glossary));
        Assert.All(
            records,
            record =>
            {
                Assert.StartsWith(
                    "Ingest.",
                    record.Key,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Codex focused editorial review",
                    record.Reviewer);
                Assert.Equal("2026-07-25", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Health_review_batch_contains_the_exact_focused_domain()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-health-editorial-2026-07-25";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(389, records.Length);
        Assert.Equal(
            382,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            7,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            382,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.EditorialOverride));
        Assert.Equal(
            7,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.Glossary));
        Assert.All(
            records,
            record =>
            {
                Assert.StartsWith(
                    "Health.",
                    record.Key,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Codex focused editorial review",
                    record.Reviewer);
                Assert.Equal("2026-07-25", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Library_review_batch_contains_the_exact_focused_domain()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-library-editorial-2026-07-25";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(369, records.Length);
        Assert.Equal(
            364,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            5,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            364,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.EditorialOverride));
        Assert.Equal(
            5,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.Glossary));
        Assert.All(
            records,
            record =>
            {
                Assert.StartsWith(
                    "Library.",
                    record.Key,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Codex focused editorial review",
                    record.Reviewer);
                Assert.Equal("2026-07-25", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Settings_usage_split_batch_contains_exactly_the_new_resources()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-settings-usage-splits-editorial-2026-07-25";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(
            [
                "Settings.Playlists.TransportOptions",
                "Settings.Playlists.TransportProvider",
                "Settings.RootPolicy.HealthSeverity",
            ],
            records.Select(record => record.Key));
        Assert.All(
            records,
            record =>
            {
                Assert.Equal(
                    EditorialReviewStatus.EditorialReviewed,
                    record.Status);
                Assert.Equal(
                    CatalogTranslationRoute.EditorialOverride,
                    record.Route);
                Assert.Equal(
                    "Codex focused editorial review",
                    record.Reviewer);
                Assert.Equal("2026-07-25", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Settings_review_batch_contains_the_exact_focused_domain()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-settings-editorial-2026-07-25";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(556, records.Length);
        Assert.Equal(
            457,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            99,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            457,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.EditorialOverride));
        Assert.Equal(
            99,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.Glossary));
        Assert.All(
            records,
            record =>
            {
                Assert.StartsWith(
                    "Settings.",
                    record.Key,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Codex focused editorial review",
                    record.Reviewer);
                Assert.Equal("2026-07-25", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Settings_deletion_review_batch_contains_the_exact_two_resources()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-settings-deletion-2026-07-26";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(
            [
                "Settings.Ingest.ProfileDeletionDescription",
                "Settings.RootPolicy.BuiltInDescription",
            ],
            records.Select(record => record.Key));
        Assert.All(
            records,
            record =>
            {
                Assert.Equal(
                    EditorialReviewStatus.EditorialReviewed,
                    record.Status);
                Assert.Equal(
                    CatalogTranslationRoute.EditorialOverride,
                    record.Route);
                Assert.Equal(
                    "Codex Settings deletion review",
                    record.Reviewer);
                Assert.Equal("2026-07-26", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Workbench_review_batch_contains_the_exact_focused_domain()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        const string batch =
            "gui-usability-workbench-editorial-2026-07-26";
        EditorialReviewRecord[] records =
            manifest.Records.Values
                .Where(record =>
                    string.Equals(
                        record.Batch,
                        batch,
                        StringComparison.Ordinal))
                .OrderBy(
                    record => record.Key,
                    StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(406, records.Length);
        Assert.Equal(
            372,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            34,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            372,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.EditorialOverride));
        Assert.Equal(
            34,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.Glossary));
        Assert.All(
            records,
            record =>
            {
                Assert.StartsWith(
                    "Workbench.",
                    record.Key,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Codex Workbench editorial review",
                    record.Reviewer);
                Assert.Equal("2026-07-26", record.Date);
                Assert.StartsWith(
                    "packet:v1:",
                    record.Disposition,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void Workbench_source_reconciliation_batches_contain_the_exact_reviewed_contract()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: true);
        (string Batch, string Prefix, int Count)[] batches =
        [
            (
                "gui-usability-workbench-source-reconciliation-2026-07-26",
                "Workbench.",
                61),
            (
                "gui-usability-column-source-reconciliation-2026-07-26",
                "Column.",
                9),
        ];

        Assert.Equal(
            70,
            WorkbenchSourceReconciliationContract
                .AddedOrChangedResources.Length);
        foreach ((string batch, string prefix, int count) in batches)
        {
            string[] expectedKeys =
            [
                .. WorkbenchSourceReconciliationContract
                    .AddedOrChangedResources
                    .Where(key =>
                        key.StartsWith(
                            prefix,
                            StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal),
            ];
            EditorialReviewRecord[] records =
            [
                .. manifest.Records.Values
                    .Where(record =>
                        string.Equals(
                            record.Batch,
                            batch,
                            StringComparison.Ordinal))
                    .OrderBy(
                        record => record.Key,
                        StringComparer.Ordinal),
            ];

            Assert.Equal(count, expectedKeys.Length);
            Assert.Equal(
                expectedKeys,
                records.Select(record => record.Key));
            Assert.All(
                records,
                record =>
                {
                    Assert.Equal(
                        EditorialReviewStatus.EditorialReviewed,
                        record.Status);
                    Assert.Equal(
                        CatalogTranslationRoute.EditorialOverride,
                        record.Route);
                    Assert.Equal(
                        "Codex Workbench source reconciliation",
                        record.Reviewer);
                    Assert.Equal("2026-07-26", record.Date);
                    Assert.StartsWith(
                        "packet:v1:",
                        record.Disposition,
                        StringComparison.Ordinal);
                });
        }
    }

    [Fact]
    public void Editor_review_batches_have_exact_live_manifest_provenance()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: true);
        (
            string Batch,
            string Prefix,
            int Total,
            int Editorial,
            int Glossary)[] batches =
        [
            (
                "gui-usability-inspector-editorial-2026-07-26",
                "Inspector.",
                104,
                103,
                1),
            (
                "gui-usability-column-editorial-2026-07-26",
                "Column.",
                48,
                17,
                31),
            (
                "gui-usability-fields-editorial-2026-07-26",
                "Fields.",
                33,
                33,
                0),
            (
                "gui-usability-reviewed-file-operation-editorial-2026-07-26",
                "ReviewedFileOperation.",
                32,
                32,
                0),
        ];

        foreach ((
                     string batch,
                     string prefix,
                     int total,
                     int editorial,
                     int glossary) in batches)
        {
            EditorialReviewRecord[] records =
            [
                .. manifest.Records.Values
                    .Where(record =>
                        string.Equals(
                            record.Batch,
                            batch,
                            StringComparison.Ordinal)),
            ];

            Assert.Equal(total, records.Length);
            Assert.Equal(
                editorial,
                records.Count(record =>
                    record.Status ==
                        EditorialReviewStatus.EditorialReviewed &&
                    record.Route ==
                        CatalogTranslationRoute.EditorialOverride));
            Assert.Equal(
                glossary,
                records.Count(record =>
                    record.Status ==
                        EditorialReviewStatus.GlossaryReviewed &&
                    record.Route ==
                        CatalogTranslationRoute.Glossary));
            Assert.All(
                records,
                record =>
                {
                    Assert.StartsWith(
                        prefix,
                        record.Key,
                        StringComparison.Ordinal);
                    Assert.Equal(
                        "Codex editor localization review",
                        record.Reviewer);
                    Assert.Equal("2026-07-26", record.Date);
                    Assert.StartsWith(
                        "packet:v1:",
                        record.Disposition,
                        StringComparison.Ordinal);
                });
        }
    }

    [Fact]
    public void Editor_source_reconciliation_batches_contain_the_exact_reviewed_contract()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: true);
        (
            string Batch,
            string Prefix,
            int Count,
            string PacketIdentity)[] batches =
        [
            (
                "gui-usability-inspector-source-reconciliation-2026-07-26",
                "Inspector.",
                5,
                "63971812999a7b253f6015896777aeff9103f9c914d69719d4d33f6b7211328d"),
            (
                "gui-usability-fields-source-reconciliation-2026-07-26",
                "Fields.",
                8,
                "6dd648d1e5e50bdf32db7115df0f1a789f1eb8a954d24e3148eda8e9b62b6331"),
            (
                "gui-usability-library-source-reconciliation-2026-07-26",
                "Library.",
                2,
                "3f2f746a23949289acecb8729fac2ba378258687ef91eb6ae71fb86e63b9255a"),
            (
                "gui-usability-column-editor-source-reconciliation-2026-07-26",
                "Column.",
                1,
                "ccec4f0c0ec1e4189b90e85399cad43d44a2fa984057f35010af4229f4393b1f"),
            (
                "gui-usability-reviewed-file-operation-source-reconciliation-2026-07-26",
                "ReviewedFileOperation.",
                2,
                "cab2d0914679e6b9f68c6e69e77743b880f41edbc9377140177097a05839e534"),
        ];

        Assert.Equal(
            18,
            EditorSourceReconciliationContract
                .AddedOrChangedResources.Length);
        foreach ((
                     string batch,
                     string prefix,
                     int count,
                     string packetIdentity) in batches)
        {
            string[] expectedKeys =
            [
                .. EditorSourceReconciliationContract
                    .AddedOrChangedResources
                    .Where(key =>
                        key.StartsWith(
                            prefix,
                            StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal),
            ];
            EditorialReviewRecord[] records =
            [
                .. manifest.Records.Values
                    .Where(record =>
                        string.Equals(
                            record.Batch,
                            batch,
                            StringComparison.Ordinal))
                    .OrderBy(
                        record => record.Key,
                        StringComparer.Ordinal),
            ];

            Assert.Equal(count, expectedKeys.Length);
            Assert.Equal(
                expectedKeys,
                records.Select(record => record.Key));
            Assert.All(
                records,
                record =>
                {
                    Assert.Equal(
                        EditorialReviewStatus.EditorialReviewed,
                        record.Status);
                    Assert.Equal(
                        CatalogTranslationRoute.EditorialOverride,
                        record.Route);
                    Assert.Equal(
                        "Codex editor source reconciliation",
                        record.Reviewer);
                    Assert.Equal("2026-07-26", record.Date);
                    Assert.Matches(
                        $"^packet:v1:{packetIdentity}:[0-9a-f]{{64}}$",
                        record.Disposition);
                });
        }
    }

    [Fact]
    public void Git_seed_compares_parsed_values_and_binds_reviewed_neutral()
    {
        WithTemporaryDirectory(directory =>
        {
            string overrideDirectory = Path.Combine(
                directory,
                "BuildTools",
                "LocalizationCatalogGenerator");
            string resourceDirectory = Path.Combine(
                directory,
                "MusicLibraryManager.Presentation",
                "Resources");
            Directory.CreateDirectory(overrideDirectory);
            Directory.CreateDirectory(resourceDirectory);
            string overridesPath = Path.Combine(
                overrideDirectory,
                "EditorialOverrides.xml");
            string neutralPath = Path.Combine(
                resourceDirectory,
                "Strings.resx");
            File.WriteAllText(
                overridesPath,
                CreateOverrideDocument(
                    ("Example.One", "baseline")));
            File.WriteAllText(
                neutralPath,
                CreateNeutralDocument(
                    ("Example.One", "Neutral one"),
                    ("Example.Two", "Neutral two")));
            RunGit(directory, "init");
            RunGit(
                directory,
                "config",
                "user.name",
                "Editorial Review Test");
            RunGit(
                directory,
                "config",
                "user.email",
                "editorial-review@example.invalid");
            RunGit(directory, "add", ".");
            RunGit(
                directory,
                "commit",
                "-m",
                "baseline");
            string baseline = RunGit(
                    directory,
                    "rev-parse",
                    "HEAD")
                .Trim();

            File.WriteAllText(
                overridesPath,
                CreateOverrideDocument(
                    ("Example.One", "reviewed"),
                    ("Example.Two", "added")));
            RunGit(directory, "add", ".");
            RunGit(
                directory,
                "commit",
                "-m",
                "reviewed");
            string reviewedCommit = RunGit(
                    directory,
                    "rev-parse",
                    "HEAD")
                .Trim();

            IReadOnlyDictionary<string, ReviewedCatalogEvidence>
                evidence =
                    EditorialReviewInfrastructure
                        .FindReviewedOverrideChanges(
                            directory,
                            baseline,
                            reviewedCommit);
            Assert.Equal(2, evidence.Count);
            Assert.Equal(
                "Neutral one",
                evidence["Example.One"].Neutral);
            Assert.Equal(
                "reviewed-de-DE",
                evidence["Example.One"]
                    .Translations["de-DE"]);
            Assert.Equal(
                "Neutral two",
                evidence["Example.Two"].Neutral);
        });
    }

    [Fact]
    public void Equality_never_approves_a_key_outside_the_explicit_allowlist()
    {
        var translations =
            EditorialReviewInfrastructure.ShippingCultures
                .ToDictionary(
                    culture => culture,
                    _ => "Same untranslated sentence",
                    StringComparer.Ordinal);
        CatalogReviewSource source = new(
            "Synthetic.Unreviewed",
            "Same untranslated sentence",
            translations,
            CatalogTranslationRoute.Glossary);

        WithTemporaryDirectory(directory =>
        {
            EditorialReviewManifest manifest =
                EditorialReviewInfrastructure.Refresh(
                    Path.Combine(directory, "manifest.xml"),
                    [source],
                    new Dictionary<
                        string,
                        ReviewedCatalogEvidence>(
                        StringComparer.Ordinal),
                    new Dictionary<string, string>(
                        StringComparer.Ordinal),
                    "test",
                    "Test reviewer",
                    "2026-07-25");

            Assert.Equal(
                EditorialReviewStatus.Pending,
                manifest.Records[source.Key].Status);
        });
    }

    [Fact]
    public void Audit_and_pending_review_packets_are_deterministic_and_complete()
    {
        ReviewFixture fixture = LoadFixture();
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);

        WithTemporaryDirectory(directory =>
        {
            string auditOne = Path.Combine(directory, "audit-1.tsv");
            string auditTwo = Path.Combine(directory, "audit-2.tsv");
            EditorialReviewInfrastructure.WriteAudit(
                auditOne,
                manifest);
            EditorialReviewInfrastructure.WriteAudit(
                auditTwo,
                manifest);
            Assert.Equal(
                File.ReadAllBytes(auditOne),
                File.ReadAllBytes(auditTwo));
            Assert.Equal(
                manifest.Records.Count + 1,
                File.ReadLines(auditOne).Count());

            CatalogReviewSource[] packetSources =
            [
                .. fixture.Sources
                    .Where(source =>
                        source.Key.StartsWith(
                            "Workbench.",
                            StringComparison.Ordinal))
                    .Take(2),
            ];
            Assert.Equal(2, packetSources.Length);
            EditorialReviewManifest pendingManifest =
                EditorialReviewInfrastructure.Refresh(
                    Path.Combine(
                        directory,
                        "packet-manifest.xml"),
                    packetSources,
                    new Dictionary<
                        string,
                        ReviewedCatalogEvidence>(
                        StringComparer.Ordinal),
                    new Dictionary<string, string>(
                        StringComparer.Ordinal),
                    "packet-test",
                    "Packet test reviewer",
                    "2026-07-26");
            string packetOne = Path.Combine(directory, "packet-1.xml");
            string packetTwo = Path.Combine(directory, "packet-2.xml");
            EditorialReviewInfrastructure.WriteReviewPacket(
                packetOne,
                "Workbench",
                packetSources,
                pendingManifest);
            EditorialReviewInfrastructure.WriteReviewPacket(
                packetTwo,
                "Workbench",
                packetSources,
                pendingManifest);
            Assert.Equal(
                File.ReadAllBytes(packetOne),
                File.ReadAllBytes(packetTwo));

            XDocument packet = XDocument.Load(packetOne);
            XElement[] entries =
            [
                .. packet.Root!.Elements("entry"),
            ];
            Assert.Equal(2, entries.Length);
            Assert.All(
                entries,
                entry =>
                {
                    Assert.Equal(
                        EditorialReviewStatus.Pending.ToString(),
                        (string?)entry.Attribute("status"));
                    Assert.NotNull(entry.Element("neutral"));
                    Assert.Equal(
                        9,
                        entry.Element("translations")!
                            .Elements("translation")
                            .Count());
                    Assert.NotNull(entry.Element("placeholders"));
                    Assert.NotNull(entry.Element("protected-tokens"));
                });
        });
    }

    [Fact]
    public void Review_packet_identity_rejects_domain_changes_and_injected_current_entries()
    {
        ReviewFixture fixture = LoadFixture();

        WithTemporaryDirectory(directory =>
        {
            EditorialReviewManifest manifest =
                CreateUnreviewedManifest(
                    fixture,
                    directory);
            CatalogReviewSource[] pending =
            [
                .. fixture.Sources
                    .Where(source =>
                        manifest.Records[source.Key].Status ==
                            EditorialReviewStatus.Pending)
                    .OrderBy(source => source.Key, StringComparer.Ordinal)
                    .Take(2),
            ];
            Assert.Equal(2, pending.Length);

            string firstPath = Path.Combine(
                directory,
                "first.xml");
            string secondPath = Path.Combine(
                directory,
                "second.xml");
            EditorialReviewInfrastructure.WriteReviewPacket(
                firstPath,
                pending[0].Key,
                fixture.Sources,
                manifest);
            EditorialReviewInfrastructure.WriteReviewPacket(
                secondPath,
                pending[1].Key,
                fixture.Sources,
                manifest);

            XDocument changedDomain = XDocument.Load(firstPath);
            changedDomain.Root!.SetAttributeValue("domain", "*");
            string changedDomainPath = Path.Combine(
                directory,
                "changed-domain.xml");
            changedDomain.Save(changedDomainPath);
            InvalidDataException domainFailure =
                Assert.Throws<InvalidDataException>(
                    () => EditorialReviewInfrastructure
                        .ApproveReviewPacket(
                            changedDomainPath,
                            fixture.Sources,
                            manifest,
                            "test-review",
                            "Test reviewer",
                            "2026-07-25"));
            Assert.Contains(
                "identity does not match its domain",
                domainFailure.Message,
                StringComparison.Ordinal);

            XDocument injected = XDocument.Load(firstPath);
            XDocument second = XDocument.Load(secondPath);
            injected.Root!.SetAttributeValue("domain", "*");
            injected.Root.SetAttributeValue("resourceCount", "2");
            injected.Root.Add(
                new XElement(
                    second.Root!.Element("entry")!));
            string injectedPath = Path.Combine(
                directory,
                "injected.xml");
            injected.Save(injectedPath);
            InvalidDataException injectionFailure =
                Assert.Throws<InvalidDataException>(
                    () => EditorialReviewInfrastructure
                        .ApproveReviewPacket(
                            injectedPath,
                            fixture.Sources,
                            manifest,
                            "test-review",
                            "Test reviewer",
                            "2026-07-25"));
            Assert.Contains(
                "identity is inconsistent",
                injectionFailure.Message,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Atomic_output_batch_rolls_back_every_applied_target_before_commit()
    {
        WithTemporaryDirectory(directory =>
        {
            string first = Path.Combine(directory, "a-first.txt");
            string second = Path.Combine(directory, "b-second.txt");
            File.WriteAllText(first, "first-original");
            File.WriteAllText(second, "second-original");
            int applyCount = 0;
            var hooks = new AtomicOutputBatchHooks(
                BeforeApply: _ =>
                {
                    applyCount++;
                    if (applyCount == 2)
                        throw new IOException(
                            "Injected apply failure.");
                });

            IOException failure = Assert.Throws<IOException>(
                () => AtomicOutputBatch.Commit(
                    new Dictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        [first] = "first-replacement",
                        [second] = "second-replacement",
                    },
                    hooks));
            Assert.Contains(
                "Injected apply failure",
                failure.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                "first-original",
                File.ReadAllText(first));
            Assert.Equal(
                "second-original",
                File.ReadAllText(second));
            Assert.Empty(
                Directory.EnumerateFiles(
                    directory,
                    ".*",
                    SearchOption.TopDirectoryOnly));
        });
    }

    [Fact]
    public void Atomic_output_batch_reports_cleanup_failure_as_post_commit()
    {
        WithTemporaryDirectory(directory =>
        {
            string first = Path.Combine(directory, "a-first.txt");
            string second = Path.Combine(directory, "b-second.txt");
            File.WriteAllText(first, "first-original");
            File.WriteAllText(second, "second-original");
            bool injected = false;
            var hooks = new AtomicOutputBatchHooks(
                BeforeBackupCleanup: _ =>
                {
                    if (!injected)
                    {
                        injected = true;
                        throw new IOException(
                            "Injected cleanup failure.");
                    }
                });

            AtomicOutputCleanupException failure =
                Assert.Throws<AtomicOutputCleanupException>(
                    () => AtomicOutputBatch.Commit(
                        new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            [first] = "first-replacement",
                            [second] = "second-replacement",
                        },
                        hooks));
            Assert.Contains(
                "committed successfully",
                failure.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                "first-replacement",
                File.ReadAllText(first));
            Assert.Equal(
                "second-replacement",
                File.ReadAllText(second));
            Assert.Contains(
                Directory.EnumerateFiles(
                    directory,
                    "*.backup",
                    SearchOption.TopDirectoryOnly),
                path => File.Exists(path));
        });
    }

    [Fact]
    public void Approved_packet_advances_pending_routes_without_hand_editing()
    {
        ReviewFixture fixture = LoadFixture();

        WithTemporaryDirectory(directory =>
        {
            EditorialReviewManifest original =
                CreateUnreviewedManifest(
                    fixture,
                    directory);
            CatalogReviewSource glossaryPending =
                fixture.Sources.First(source =>
                    original.Records[source.Key].Status ==
                        EditorialReviewStatus.Pending &&
                    source.Route !=
                        CatalogTranslationRoute.EditorialOverride);
            CatalogReviewSource editorialPending =
                fixture.Sources.First(source =>
                    original.Records[source.Key].Status ==
                        EditorialReviewStatus.Pending &&
                    source.Route ==
                        CatalogTranslationRoute.EditorialOverride);
            string manifestPath =
                Path.Combine(directory, "manifest.xml");
            File.WriteAllText(
                manifestPath,
                EditorialReviewInfrastructure.SerializeManifest(
                    original));
            string packetPath =
                Path.Combine(directory, "packet.xml");
            EditorialReviewInfrastructure.WriteReviewPacket(
                packetPath,
                glossaryPending.Key,
                fixture.Sources,
                original);

            EditorialReviewManifest approved =
                EditorialReviewInfrastructure.ApproveReviewPacket(
                    packetPath,
                    fixture.Sources,
                    original,
                    "domain-review-1",
                    "Reviewer Name",
                    "2026-07-25");
            EditorialReviewRecord record =
                approved.Records[glossaryPending.Key];
            Assert.Equal(
                EditorialReviewStatus.GlossaryReviewed,
                record.Status);
            Assert.Equal("domain-review-1", record.Batch);
            Assert.Equal("Reviewer Name", record.Reviewer);

            AtomicOutputBatch.Commit(
                new Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    [manifestPath] =
                        EditorialReviewInfrastructure.SerializeManifest(
                            approved),
                });
            EditorialReviewManifest reloaded =
                EditorialReviewInfrastructure.LoadAndValidate(
                    manifestPath,
                    fixture.Sources,
                    new Dictionary<string, string>(
                        StringComparer.Ordinal),
                    requireComplete: false);
            Assert.Equal(
                EditorialReviewStatus.GlossaryReviewed,
                reloaded.Records[glossaryPending.Key].Status);
            Assert.Equal(
                Count(
                    original,
                    EditorialReviewStatus.Pending) -
                1,
                Count(
                    reloaded,
                    EditorialReviewStatus.Pending));

            string editorialPacketPath =
                Path.Combine(directory, "editorial-packet.xml");
            EditorialReviewInfrastructure.WriteReviewPacket(
                editorialPacketPath,
                editorialPending.Key,
                fixture.Sources,
                reloaded);
            XDocument stalePacket =
                XDocument.Load(editorialPacketPath);
            XAttribute digest =
                stalePacket.Root!
                    .Element("entry")!
                    .Attribute("digest")!;
            digest.Value =
                (digest.Value[0] == '0' ? "1" : "0") +
                digest.Value[1..];
            stalePacket.Save(editorialPacketPath);
            InvalidDataException stale =
                Assert.Throws<InvalidDataException>(
                    () =>
                        EditorialReviewInfrastructure.ApproveReviewPacket(
                            editorialPacketPath,
                            fixture.Sources,
                            reloaded,
                            "domain-review-2",
                            "Reviewer Name",
                            "2026-07-25"));
            Assert.Contains(
                "digest is stale",
                stale.Message,
                StringComparison.Ordinal);

            EditorialReviewInfrastructure.WriteReviewPacket(
                editorialPacketPath,
                editorialPending.Key,
                fixture.Sources,
                reloaded);
            EditorialReviewManifest editorialApproved =
                EditorialReviewInfrastructure.ApproveReviewPacket(
                    editorialPacketPath,
                    fixture.Sources,
                    reloaded,
                    "domain-review-2",
                    "Reviewer Name",
                    "2026-07-25");
            Assert.Equal(
                EditorialReviewStatus.EditorialReviewed,
                editorialApproved.Records[editorialPending.Key].Status);
            Assert.Equal(
                Count(
                    original,
                    EditorialReviewStatus.Pending) -
                2,
                Count(
                    editorialApproved,
                    EditorialReviewStatus.Pending));
        });
    }

    private static EditorialReviewManifest
        CreateUnreviewedManifest(
            ReviewFixture fixture,
            string directory) =>
        EditorialReviewInfrastructure.Refresh(
            Path.Combine(
                directory,
                "unreviewed-source.xml"),
            fixture.Sources,
            new Dictionary<
                string,
                ReviewedCatalogEvidence>(
                StringComparer.Ordinal),
            new Dictionary<string, string>(
                StringComparer.Ordinal),
            "test-backlog",
            "Test reviewer",
            "2026-07-26");

    private static ReviewFixture LoadFixture()
    {
        string repositoryRoot = FindRepositoryRoot();
        string generatorDirectory = Path.Combine(
            repositoryRoot,
            "BuildTools",
            "LocalizationCatalogGenerator");
        string manifestPath = Path.Combine(
            generatorDirectory,
            EditorialReviewInfrastructure.DefaultManifestFileName);
        string reviewEvidencePath = Path.Combine(
            generatorDirectory,
            "FocusedEditorialReviewEvidence.v1.xml");
        XDocument manifest = XDocument.Load(manifestPath);
        var routes = manifest.Root!
            .Elements("entry")
            .ToDictionary(
                entry =>
                    (string?)entry.Attribute("key") ?? "",
                entry => Enum.Parse<CatalogTranslationRoute>(
                    (string?)entry.Attribute("route") ?? ""),
                StringComparer.Ordinal);

        string resourcesDirectory = Path.Combine(
            repositoryRoot,
            "MusicLibraryManager.Presentation",
            "Resources");
        XDocument neutral = XDocument.Load(
            Path.Combine(
                resourcesDirectory,
                "Strings.resx"));
        var translationsByCulture = new Dictionary<
            string,
            IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal);
        foreach (string culture in
                 EditorialReviewInfrastructure.ShippingCultures)
        {
            XDocument satellite = XDocument.Load(
                Path.Combine(
                    resourcesDirectory,
                    $"Strings.{culture}.resx"));
            translationsByCulture[culture] =
                satellite.Root!
                    .Elements("data")
                    .ToDictionary(
                        entry =>
                            (string?)entry.Attribute("name") ?? "",
                        entry =>
                            entry.Element("value")?.Value ?? "",
                        StringComparer.Ordinal);
        }

        CatalogReviewSource[] sources =
        [
            .. neutral.Root!
                .Elements("data")
                .Select(entry =>
                {
                    string key =
                        (string?)entry.Attribute("name") ?? "";
                    return new CatalogReviewSource(
                        key,
                        entry.Element("value")?.Value ?? "",
                        translationsByCulture.ToDictionary(
                            item => item.Key,
                            item => item.Value[key],
                            StringComparer.Ordinal),
                        routes[key]);
                }),
        ];
        IReadOnlyDictionary<string, string> allowlist =
            EditorialReviewInfrastructure.LoadInvariantAllowlist(
                Path.Combine(
                    generatorDirectory,
                    "InvariantApprovedValues.v1.tsv"));
        return new ReviewFixture(
            repositoryRoot,
            manifestPath,
            reviewEvidencePath,
            sources,
            allowlist);
    }

    private static int Count(
        EditorialReviewManifest manifest,
        EditorialReviewStatus status) =>
        manifest.Records.Values.Count(record =>
            record.Status == status);

    private static string ReadManifestDigest(
        string serializedManifest)
    {
        using var reader = new StringReader(serializedManifest);
        XDocument document = XDocument.Load(reader);
        return (string?)document.Root!
            .Attribute("manifestDigest") ??
            throw new InvalidDataException(
                "Serialized manifest has no manifestDigest.");
    }

    private static string FlipDigest(string digest) =>
        (digest[0] == '0' ? "1" : "0") + digest[1..];

    private static string CreateOverrideDocument(
        params (string Key, string Value)[] entries)
    {
        var root = new XElement("editorial-overrides");
        foreach ((string key, string value) in entries)
        {
            var entry = new XElement(
                "entry",
                new XAttribute("key", key));
            foreach (string culture in
                     EditorialReviewInfrastructure.ShippingCultures)
                entry.Add(
                    new XElement(
                        "translation",
                        new XAttribute("culture", culture),
                        $"{value}-{culture}"));
            root.Add(entry);
        }
        return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                root)
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string CreateNeutralDocument(
        params (string Key, string Value)[] entries)
    {
        var root = new XElement("root");
        foreach ((string key, string value) in entries)
            root.Add(
                new XElement(
                    "data",
                    new XAttribute("name", key),
                    new XAttribute(
                        XNamespace.Xml + "space",
                        "preserve"),
                    new XElement("value", value)));
        return new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                root)
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string RunGit(
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException(
                "Could not start git for the isolated provenance test.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private static void WithTemporaryDirectory(
        Action<string> action)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "mlm-editorial-review-" +
            Guid.NewGuid().ToString(
                "N",
                CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        try
        {
            action(directory);
        }
        finally
        {
            foreach (string file in
                     Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.AllDirectories))
                File.SetAttributes(
                    file,
                    FileAttributes.Normal);
            Directory.Delete(
                directory,
                recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current =
            new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "MusicLibraryTools.sln")) &&
                Directory.Exists(Path.Combine(
                    current.FullName,
                    "BuildTools",
                    "LocalizationCatalogGenerator")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the MusicLibraryTools repository root.");
    }

    private sealed record ReviewFixture(
        string RepositoryRoot,
        string ManifestPath,
        string ReviewEvidencePath,
        IReadOnlyList<CatalogReviewSource> Sources,
        IReadOnlyDictionary<string, string> InvariantApprovedValues);
}

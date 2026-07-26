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

        Assert.Equal(3_544, manifest.Records.Count);
        Assert.Equal(
            2_200,
            Count(
                manifest,
                EditorialReviewStatus.Pending));
        Assert.Equal(
            115,
            Count(
                manifest,
                EditorialReviewStatus.InvariantApproved));
        Assert.Equal(
            44,
            Count(
                manifest,
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            1_185,
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
            manifest.Records.Values.First(record =>
                record.Status ==
                    EditorialReviewStatus.Pending);
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
    public void Manifest_rejects_canonical_status_provenance_swap_and_refresh_cannot_launder_it()
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
        XElement pending = checkedIn.Root!
            .Elements("entry")
            .First(entry =>
                string.Equals(
                    (string?)entry.Attribute("status"),
                    EditorialReviewStatus.Pending.ToString(),
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)entry.Attribute("route"),
                    CatalogTranslationRoute
                        .EditorialOverride.ToString(),
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
                (string)pending.Attribute(attributeName)!);
            pending.SetAttributeValue(
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
            Assert.Contains(
                "manifestDigest does not match",
                loadFailure.Message,
                StringComparison.Ordinal);

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
            Assert.Contains(
                "manifestDigest does not match",
                refreshFailure.Message,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Strict_gate_fails_without_consuming_pending_review_state()
    {
        ReviewFixture fixture = LoadFixture();

        InvalidDataException exception =
            Assert.Throws<InvalidDataException>(
                () => EditorialReviewInfrastructure.LoadAndValidate(
                    fixture.ManifestPath,
                    fixture.Sources,
                    fixture.InvariantApprovedValues,
                    requireComplete: true));

        Assert.Equal(
            "Strict editorial review failed: 2,200 resources remain Pending.",
            exception.Message);
        EditorialReviewManifest unchanged =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        Assert.Equal(
            2_200,
            Count(
                unchanged,
                EditorialReviewStatus.Pending));
    }

    [Fact]
    public void Checked_in_evidence_independently_reproduces_reviewed_provenance()
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
        Assert.Equal(847, seed.Catalogs.Count);
        Assert.Equal(
            "gui-usability-editorial-2026-07-25",
            seed.Batch);
        Assert.Equal(
            "Codex focused editorial batches",
            seed.Reviewer);
        Assert.Equal("2026-07-25", seed.Date);
        Assert.All(
            seed.Catalogs,
            item =>
            {
                Assert.StartsWith(
                    "review-set:v1:",
                    item.Value.Disposition,
                    StringComparison.Ordinal);
                Assert.Equal(
                    CatalogTranslationRoute.EditorialOverride,
                    current.Records[item.Key].Route);
                Assert.Equal(
                    EditorialReviewStatus.EditorialReviewed,
                    current.Records[item.Key].Status);
            });

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
                1_185,
                Count(
                    refreshed,
                    EditorialReviewStatus.EditorialReviewed));
            Assert.Equal(
                115,
                Count(
                    refreshed,
                    EditorialReviewStatus.InvariantApproved));
            Assert.Equal(
                2_200,
                Count(
                    refreshed,
                    EditorialReviewStatus.Pending));
            Assert.Equal(
                44,
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

        Assert.Equal(212, records.Length);
        Assert.Equal(
            176,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.EditorialReviewed));
        Assert.Equal(
            36,
            records.Count(record =>
                record.Status ==
                EditorialReviewStatus.GlossaryReviewed));
        Assert.Equal(
            176,
            records.Count(record =>
                record.Route ==
                CatalogTranslationRoute.EditorialOverride));
        Assert.Equal(
            35,
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
                3_545,
                File.ReadLines(auditOne).Count());

            string packetOne = Path.Combine(directory, "packet-1.xml");
            string packetTwo = Path.Combine(directory, "packet-2.xml");
            EditorialReviewInfrastructure.WriteReviewPacket(
                packetOne,
                "Workbench",
                fixture.Sources,
                manifest);
            EditorialReviewInfrastructure.WriteReviewPacket(
                packetTwo,
                "Workbench",
                fixture.Sources,
                manifest);
            Assert.Equal(
                File.ReadAllBytes(packetOne),
                File.ReadAllBytes(packetTwo));

            XDocument packet = XDocument.Load(packetOne);
            XElement[] entries =
            [
                .. packet.Root!.Elements("entry"),
            ];
            Assert.NotEmpty(entries);
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
        EditorialReviewManifest manifest =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
        CatalogReviewSource[] pending =
        [
            .. fixture.Sources
                .Where(source =>
                    manifest.Records[source.Key].Status ==
                        EditorialReviewStatus.Pending)
                .Take(2),
        ];
        Assert.Equal(2, pending.Length);

        WithTemporaryDirectory(directory =>
        {
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
        EditorialReviewManifest original =
            EditorialReviewInfrastructure.LoadAndValidate(
                fixture.ManifestPath,
                fixture.Sources,
                fixture.InvariantApprovedValues,
                requireComplete: false);
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

        WithTemporaryDirectory(directory =>
        {
            string manifestPath =
                Path.Combine(directory, "manifest.xml");
            File.Copy(
                fixture.ManifestPath,
                manifestPath);
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
                    fixture.InvariantApprovedValues,
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

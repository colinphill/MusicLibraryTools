using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class LibraryHealthPolicyServiceTests
{
    [Fact]
    public void BuiltInRulesExposeStableIdentityAndApplicability()
    {
        Assert.Equal(BuiltInHealthRules.All.Count,
            BuiltInHealthRules.All.Select(rule => rule.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(BuiltInHealthRules.All, rule =>
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
            Assert.False(string.IsNullOrWhiteSpace(rule.Applicability));
            Assert.Equal(rule.Id, rule.DefaultPolicy.Id);
        });
    }

    [Fact]
    public void LossyFindingCanBeInformationalOrDisabled()
    {
        TrackRecord record = Track("song.mp3", disc: 1) with
        {
            CodecName = "MP3",
            CodecType = CodecType.Lossy,
        };
        var informational = new LibraryHealthPolicy(
        [
            new(LibraryHealthRuleIds.LossyFile, true,
                LibraryHealthSeverity.Information, false, false),
        ]);

        AnalysisFinding finding = Assert.Single(
            LibraryAnalyzer.Lossless([record], informational).Findings);

        Assert.Equal(LibraryHealthRuleIds.LossyFile, finding.RuleId);
        Assert.Equal(LibraryHealthSeverity.Information, finding.Severity);

        var disabled = new LibraryHealthPolicy(
        [
            new(LibraryHealthRuleIds.LossyFile, false,
                LibraryHealthSeverity.Information, false, false),
        ]);
        Assert.Empty(LibraryAnalyzer.Lossless([record], disabled).Findings);

        // The original overload remains the legacy warning-producing behavior.
        Assert.Equal(LibraryHealthSeverity.Warning,
            Assert.Single(LibraryAnalyzer.Lossless([record]).Findings).Severity);
    }

    [Fact]
    public void SafePreviewExcludesDiscTitleMigrationWhenProposalIsDisabled()
    {
        string album = Path.Combine("library", "Artist", "Album");
        TrackRecord[] records =
        [
            Track(Path.Combine(album, "Disc 1", "01.flac"), disc: 1),
            Track(Path.Combine(album, "Disc 2", "01.flac"), disc: 2),
        ];
        var service = new AnalysisRepairService(new RecordingWriter());

        AnalysisRepairPlan legacy = service.PreviewSafeRepairs(records);
        Assert.Equal(2, legacy.Items.Count);
        Assert.All(legacy.Items, repair =>
            Assert.Equal(LibraryHealthRuleIds.DiscAlbumTitle, repair.RuleId));

        LibraryHealthPolicy policy = WithRule(
            LibraryHealthRuleIds.DiscAlbumTitle,
            rule => rule with { Enabled = true, ProposeRepair = false });
        AnalysisRepairPlan filtered = service.PreviewSafeRepairs(records, policy);

        Assert.Empty(filtered.Items);
    }

    [Fact]
    public async Task PolicyAwareApplyExcludesRepairsWithoutApplyPermission()
    {
        string path = Path.Combine(Path.GetTempPath(), $"health_{Guid.NewGuid():N}.flac");
        await File.WriteAllTextAsync(path, "test");
        try
        {
            var info = new FileInfo(path);
            var discRepair = new AnalysisTagRepair(
                path, TagFields.Album, "Album", "Album (Disc 1)", "disc title",
                info.Length, info.LastWriteTimeUtc,
                RuleId: LibraryHealthRuleIds.DiscAlbumTitle);
            var textRepair = new AnalysisTagRepair(
                path, TagFields.Title, " Song ", "Song", "whitespace",
                info.Length, info.LastWriteTimeUtc,
                RuleId: LibraryHealthRuleIds.NormalizeWhitespace);
            var plan = new AnalysisRepairPlan("Policy apply", [discRepair, textRepair]);
            LibraryHealthPolicy policy = WithRules(
                rule => rule.Id == LibraryHealthRuleIds.DiscAlbumTitle
                    ? rule with { Enabled = true, ApplyRepair = false }
                    : rule.Id == LibraryHealthRuleIds.NormalizeWhitespace
                        ? rule with { Enabled = true, ApplyRepair = true }
                        : rule);
            var writer = new RecordingWriter();
            var service = new AnalysisRepairService(writer);

            BatchWriteResult result = await service.ApplyAsync(plan, policy);

            Assert.Single(result.Files);
            var call = Assert.Single(writer.Calls);
            TagEdit edit = Assert.Single(call.Edits);
            Assert.Equal(TagFields.Title, edit.Field);
            Assert.Equal("Song", edit.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingRuleEntriesInheritLegacyProposalBehavior()
    {
        var plan = new AnalysisRepairPlan("Partial policy",
        [
            new AnalysisTagRepair(
                "track.flac", TagFields.AlbumArtist, null, "Artist", "missing",
                1, DateTime.UnixEpoch,
                RuleId: LibraryHealthRuleIds.MissingAlbumArtist),
        ]);
        var partialPolicy = new LibraryHealthPolicy([]);

        AnalysisRepairPlan filtered = LibraryHealthPolicyService.Default
            .FilterProposedRepairs(plan, partialPolicy);

        Assert.Single(filtered.Items);
    }

    [Fact]
    public void ReportsAndRepairsResolveHealthRulesFromEachMostSpecificRoot()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "root-health-policy-" + Guid.NewGuid().ToString("N"));
        string enabledRoot = Path.Combine(directory, "enabled");
        string disabledRoot = Path.Combine(directory, "disabled");
        Directory.CreateDirectory(enabledRoot);
        Directory.CreateDirectory(disabledRoot);
        try
        {
            LibraryProfile baseline = LibraryProfilePresets.Create(
                LibraryProfilePreset.PreserveLayoutAndTags);
            LibraryProfile enabled = baseline with
            {
                Id = "health-enabled",
                Name = "Health enabled",
                Health = new(baseline.Health.Rules.Select(rule => rule.Id switch
                {
                    LibraryHealthRuleIds.LossyFile => rule with
                    {
                        Enabled = true,
                        Severity = LibraryHealthSeverity.Error,
                    },
                    LibraryHealthRuleIds.MissingAlbumArtist => rule with
                    {
                        Enabled = true,
                        ProposeRepair = true,
                        ApplyRepair = true,
                    },
                    _ => rule,
                }).ToArray()),
            };
            LibraryProfile disabled = baseline with
            {
                Id = "health-disabled",
                Name = "Health disabled",
                Health = new(baseline.Health.Rules.Select(rule => rule.Id switch
                {
                    LibraryHealthRuleIds.LossyFile => rule with { Enabled = false },
                    LibraryHealthRuleIds.MissingAlbumArtist => rule with
                    {
                        Enabled = true,
                        ProposeRepair = false,
                        ApplyRepair = false,
                    },
                    _ => rule,
                }).ToArray()),
            };
            var editable = EditableLibraryConfig.CreateNew();
            editable.Profiles.Add(enabled);
            editable.Profiles.Add(disabled);
            IndexTargetEntry first = editable.CreateIndexTarget(enabledRoot);
            first.ProfileId = enabled.Id;
            first.Permissions = LibraryRootPermissions.WriteMetadata;
            editable.IndexTargets.Add(first);
            IndexTargetEntry second = editable.CreateIndexTarget(disabledRoot);
            second.ProfileId = disabled.Id;
            second.Permissions = LibraryRootPermissions.WriteMetadata;
            editable.IndexTargets.Add(second);
            string configPath = Path.Combine(directory, "library.xml");
            editable.Save(configPath);
            var configuration = new LibraryConfiguration(configPath);
            string enabledPath = Path.Combine(enabledRoot, "one.mp3");
            string disabledPath = Path.Combine(disabledRoot, "two.mp3");
            var service = LibraryHealthPolicyService.Default;

            AnalysisReport report = service.ApplyToReport(new("Lossy",
            [
                new(enabledPath, "lossy", "Lossy", LibraryHealthRuleIds.LossyFile),
                new(disabledPath, "lossy", "Lossy", LibraryHealthRuleIds.LossyFile),
            ]), configuration);

            AnalysisFinding finding = Assert.Single(report.Findings);
            Assert.Equal(enabledPath, finding.Path);
            Assert.Equal(LibraryHealthSeverity.Error, finding.Severity);

            var plan = new AnalysisRepairPlan("Album artists",
            [
                new(enabledPath, TagFields.AlbumArtist, null, "Artist", "missing", 1,
                    DateTime.UnixEpoch, RuleId: LibraryHealthRuleIds.MissingAlbumArtist),
                new(disabledPath, TagFields.AlbumArtist, null, "Artist", "missing", 1,
                    DateTime.UnixEpoch, RuleId: LibraryHealthRuleIds.MissingAlbumArtist),
            ]);
            Assert.Equal(enabledPath, Assert.Single(
                service.FilterProposedRepairs(plan, configuration).Items).Path);
            Assert.Equal(enabledPath, Assert.Single(
                service.FilterApplicableRepairs(plan, configuration).Items).Path);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }

    private static TrackRecord Track(string path, int disc) => new()
    {
        Path = path,
        Album = "Album",
        Artist = "Artist",
        AlbumArtist = "Artist",
        HasAlbumArtist = true,
        Title = "Song",
        TrackNumber = 1,
        TrackTotal = 1,
        DiscNumber = disc,
        DiscTotal = 2,
        CodecName = "FLAC",
        CodecType = CodecType.Lossless,
        Length = 10,
        LastWriteTime = DateTime.UtcNow,
    };

    private static LibraryHealthPolicy WithRule(
        string id,
        Func<LibraryHealthRulePolicy, LibraryHealthRulePolicy> transform) =>
        WithRules(rule => string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase)
            ? transform(rule)
            : rule);

    private static LibraryHealthPolicy WithRules(
        Func<LibraryHealthRulePolicy, LibraryHealthRulePolicy> transform) =>
        new(LibraryProfilePresets.Create(LibraryProfilePreset.LegacyMusicLibraryTools)
            .Health.Rules.Select(transform).ToArray());

    private sealed class RecordingWriter : ITagWriteService
    {
        public List<(IReadOnlyList<string> Paths, IReadOnlyList<TagEdit> Edits)> Calls { get; } = [];

        public Task<BatchWriteResult> ApplyAsync(
            IReadOnlyList<string> paths,
            IReadOnlyList<TagEdit> edits,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            Calls.Add((paths, edits));
            return Task.FromResult(new BatchWriteResult(paths.Select(path => new FileWriteResult
            {
                Path = path,
                Outcome = WriteOutcome.Saved,
            }).ToArray()));
        }
    }
}

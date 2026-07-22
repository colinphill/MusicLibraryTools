using System.Collections.Immutable;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using Xunit;

namespace MusicLibrary.Core.Tests;

public sealed class ExportProfileTests
{
    [Fact]
    public void SpecializedBuiltInsRemainHiddenUntilExplicitlyConfigured()
    {
        Assert.All(BuiltInExportProfiles.All, profile =>
        {
            Assert.False(profile.Enabled);
            Assert.False(profile.IsVisible);
            Assert.Equal(ExportTransformMode.SpecializedProvider, profile.Transform.Mode);
        });
        Assert.Empty(BuiltInExportProfiles.Visible(BuiltInExportProfiles.All));

        LibraryExportProfile configured = BuiltInExportProfiles.CarCard with
        {
            Enabled = true,
            Transport = new(LocalFileSystemExportTransport.ProviderId, @"C:\MusicCard"),
        };

        LibraryExportProfile visible = Assert.Single(
            BuiltInExportProfiles.Visible(BuiltInExportProfiles.All.Append(configured)));
        Assert.Equal("car-card", visible.Id);
    }

    [Fact]
    public void FingerprintCoversEveryExportPolicyAndNormalizesSelectionOrder()
    {
        LibraryExportProfile first = Profile("destination") with
        {
            Selection = ExportSelectionPolicy.FromPlaylists(["Road", "Favorites"]),
            Transport = new(LocalFileSystemExportTransport.ProviderId, "destination",
                ImmutableDictionary<string, string>.Empty
                    .Add("serial", "one")
                    .Add("mode", "safe")),
        };
        LibraryExportProfile reordered = first with
        {
            Selection = ExportSelectionPolicy.FromPlaylists(["Favorites", "Road"]),
            Transport = first.Transport with
            {
                Options = ImmutableDictionary<string, string>.Empty
                    .Add("mode", "safe")
                    .Add("serial", "one"),
            },
        };

        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint,
            (first with { Artwork = first.Artwork with { FrontCoverOnly = false } }).Fingerprint);
        Assert.NotEqual(first.Fingerprint,
            (first with { Reconciliation = first.Reconciliation with
            {
                ExtraFiles = ExportExtraFileDisposition.Delete,
            }}).Fingerprint);
    }

    [Fact]
    public void LocalTransportBlocksDisabledAndMismatchedDestinations()
    {
        var executor = new RecordingExecutor();
        var transport = new LocalFileSystemExportTransport(executor);
        string plannedDestination = Path.GetFullPath("planned");
        var mutations = new FileMutationPlan("test", plannedDestination, "", [], [],
            DateTimeOffset.UtcNow);

        ExportTransportPlan disabled = transport.Prepare(
            Profile(plannedDestination) with { Enabled = false }, mutations);
        ExportTransportPlan mismatch = transport.Prepare(
            Profile(Path.GetFullPath("different")), mutations);

        Assert.False(disabled.CanApply);
        Assert.Contains(disabled.Issues,
            issue => issue.Code == "export-profile-disabled");
        Assert.False(mismatch.CanApply);
        Assert.Contains(mismatch.Issues,
            issue => issue.Code == "export-destination-mismatch");
    }

    [Fact]
    public async Task LocalTransportRejectsPolicyChangeThenAppliesReviewedMutationPlan()
    {
        var executor = new RecordingExecutor();
        var transport = new LocalFileSystemExportTransport(executor);
        string destination = Path.GetFullPath("destination");
        LibraryExportProfile profile = Profile(destination);
        var mutations = new FileMutationPlan("test", destination, "", [], [],
            DateTimeOffset.UtcNow);
        ExportTransportPlan plan = transport.Prepare(profile, mutations);
        Assert.True(plan.CanApply);

        LibraryExportProfile changed = profile with
        {
            Playlists = profile.Playlists with { Enabled = true },
        };
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.ApplyAsync(plan, changed,
                ct: TestContext.Current.CancellationToken));
        Assert.Contains("changed after preview", error.Message);
        Assert.Equal(0, executor.ApplyCount);

        ExportTransportResult result = await transport.ApplyAsync(
            plan, profile, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, executor.ApplyCount);
        Assert.Same(executor.Result, result.Mutations);
        Assert.Equal(LocalFileSystemExportTransport.ProviderId, result.TransportId);
    }

    private static LibraryExportProfile Profile(string destination) => new(
        "portable",
        "Portable library",
        true,
        ExportSelectionPolicy.EntireLibrary,
        new(ExportTransformMode.Copy),
        new(PreserveSourceLayout: true),
        new(),
        new(),
        new(LocalFileSystemExportTransport.ProviderId, destination),
        new(ExportExtraFileDisposition.Quarantine));

    private sealed class RecordingExecutor : IFileMutationPlanExecutor
    {
        public int ApplyCount { get; private set; }
        public FileMutationSummary Result { get; } = new(0, 0, 0, 0, null, []);

        public Task<FileMutationSummary> ApplyAsync(FileMutationPlan plan,
            IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
        {
            ApplyCount++;
            return Task.FromResult(Result);
        }
    }
}

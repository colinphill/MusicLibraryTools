using System.ComponentModel;
using System.Globalization;
using MusicLibraryManager.Presentation;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.Tests;

[Collection(LocalizationTestCollection.Name)]
public sealed class PresentationModelsLocalizationTests
{
    [Fact]
    public void Ingest_bit_depth_choices_use_cardinal_localization()
    {
        IngestRecipeEditorRow recipe =
            IngestRecipeEditorRow.Create();
        recipe.Action =
            LibraryIngestAction.Transcode;

        LocalizedChoice<int?> sixteen =
            Assert.Single(
                recipe.TranscodeBitDepthChoices,
                choice => choice.Value == 16);

        Assert.Equal(
            "16 bits",
            sixteen.Label);
        Assert.DoesNotContain(
            "⟦",
            sixteen.Label,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Permission_and_ingest_summaries_have_no_English_runtime_fallbacks()
    {
        string source = File.ReadAllText(
            FindRepositoryFile(
                Path.Combine(
                    "MusicLibraryManager.Presentation",
                    "Models.cs")));

        Assert.DoesNotContain(
            "\"Catalog-only: this root is read-only.\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"Allowed changes: \"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "yield return \"metadata\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"configured media catalog\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"no destination\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Settings.Permissions.Summary.ReadOnly\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Settings.IngestRecipe.Summary\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Permission_fallback_refreshes_labels_without_changing_flags()
    {
        var localization =
            new SwitchingLocalizationService();
        var row = new IndexTargetEditorRow
        {
            Id = Guid.NewGuid(),
            Path = @"D:\Music",
            Permissions =
                LibraryRootPermissions.WriteMetadata |
                LibraryRootPermissions.OrganizeFiles,
        };
        row.RefreshLocalizedText(localization);
        LibraryRootPermissions permissions =
            row.Permissions;
        int summaryNotifications = 0;
        row.PropertyChanged += (
            object? _,
            PropertyChangedEventArgs args) =>
        {
            if (args.PropertyName ==
                nameof(
                    IndexTargetEditorRow.PermissionSummary))
                summaryNotifications++;
        };
        string english =
            row.PermissionSummary;

        localization.SetCulture("fr-FR");
        row.RefreshLocalizedText(localization);

        Assert.Equal(
            permissions,
            row.Permissions);
        Assert.True(
            row.AllowMetadataWrites);
        Assert.True(
            row.AllowOrganization);
        Assert.False(
            row.AllowArtworkWrites);
        Assert.NotEqual(
            english,
            row.PermissionSummary);
        Assert.Contains(
            "fr-FR:Settings.Permissions.Metadata",
            row.PermissionSummary,
            StringComparison.Ordinal);
        Assert.Contains(
            "fr-FR:Settings.Permissions.Organization",
            row.PermissionSummary,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            summaryNotifications);
    }

    [Fact]
    public async Task Settings_culture_refresh_preserves_root_and_recipe_identity_and_values()
    {
        var localization =
            new SwitchingLocalizationService();
        var viewModel = new SettingsViewModel(
            new FakeSettings(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeTheme(),
            localization: localization);
        await viewModel.NewConfigurationCommand
            .ExecuteAsync(null);
        IndexTargetEditorRow root =
            Assert.Single(
                viewModel.IndexTargets);
        root.Path =
            @"D:\User Music";
        root.AllowMetadataWrites = true;
        viewModel.AddIngestRecipeCommand
            .Execute(null);
        IngestRecipeEditorRow recipe =
            Assert.Single(
                Assert.IsType<
                    IngestProfileEditorRow>(
                    viewModel.AdvancedIngestProfile)
                    .Recipes);
        recipe.Name =
            "User archival recipe";
        recipe.InputExtensions =
            ".flac, .wav";
        recipe.Action =
            LibraryIngestAction.Transcode;
        recipe.AddToMediaCatalog = true;
        Assert.Contains(
            "en-US:Settings.IngestRecipe.Destination.ConfiguredMediaCatalog",
            recipe.Summary,
            StringComparison.Ordinal);
        recipe.DestinationRootChoice =
            Assert.Single(
                recipe.DestinationRootChoices,
                choice =>
                    choice.Id == root.Id);
        LibraryIngestRecipe source =
            recipe.Source;
        Guid rootId = root.Id;
        Guid? destinationRootId =
            recipe.DestinationRootId;
        string permissionSummary =
            root.PermissionSummary;
        string recipeSummary =
            recipe.Summary;

        Assert.Contains(
            @"D:\User Music",
            recipeSummary,
            StringComparison.Ordinal);

        localization.SetCulture("fr-FR");

        Assert.Same(
            root,
            Assert.Single(
                viewModel.IndexTargets));
        Assert.Equal(
            rootId,
            root.Id);
        Assert.True(
            root.AllowMetadataWrites);
        Assert.NotEqual(
            permissionSummary,
            root.PermissionSummary);
        Assert.StartsWith(
            "fr-FR:",
            root.PermissionSummary,
            StringComparison.Ordinal);
        Assert.Same(
            recipe,
            Assert.Single(
                Assert.IsType<
                    IngestProfileEditorRow>(
                    viewModel.AdvancedIngestProfile)
                    .Recipes));
        Assert.Same(
            source,
            recipe.Source);
        Assert.Equal(
            "User archival recipe",
            recipe.Name);
        Assert.Equal(
            ".flac, .wav",
            recipe.InputExtensions);
        Assert.Equal(
            LibraryIngestAction.Transcode,
            recipe.Action);
        Assert.Equal(
            destinationRootId,
            recipe.DestinationRootId);
        Assert.Equal(
            @"D:\User Music",
            recipe.DestinationRootChoice?.Label);
        Assert.True(
            recipe.AddToMediaCatalog);
        Assert.NotEqual(
            recipeSummary,
            recipe.Summary);
        Assert.StartsWith(
            "fr-FR:Settings.IngestRecipe.Summary",
            recipe.Summary,
            StringComparison.Ordinal);
        Assert.Contains(
            "fr-FR:Settings.Choice.LibraryIngestAction.Transcode",
            recipe.Summary,
            StringComparison.Ordinal);
        Assert.Contains(
            @"D:\User Music",
            recipe.Summary,
            StringComparison.Ordinal);
    }

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

using MusicFileUtilities;
using MusicLibraryTools;
using MusicLibraryManager.Presentation;
using Xunit;

namespace MusicLibraryManager.Tests;

public sealed class SettingsProfileDeletionGuardTests
{
    [Fact]
    public async Task Built_in_library_and_ingest_profiles_cannot_be_deleted()
    {
        SettingsViewModel viewModel = await CreateViewModelAsync();

        LibraryProfile libraryProfile = viewModel.LibraryProfiles.First(profile =>
            profile.Preset != LibraryProfilePreset.Custom);
        viewModel.SelectedLibraryProfile = libraryProfile;

        Assert.False(viewModel.CanDeleteSelectedProfile);
        Assert.False(viewModel.DeleteLibraryProfileCommand.CanExecute(null));
        int libraryProfileCount = viewModel.LibraryProfiles.Count;

        await viewModel.DeleteLibraryProfileCommand.ExecuteAsync(null);

        Assert.Equal(libraryProfileCount, viewModel.LibraryProfiles.Count);
        Assert.Contains(libraryProfile, viewModel.LibraryProfiles);

        LibraryIngestProfile ingestProfile = viewModel.IngestProfiles.First(profile =>
            LibraryIngestProfilePresets.All.Any(preset =>
                string.Equals(
                    preset.Id,
                    profile.Id,
                    StringComparison.OrdinalIgnoreCase)));
        viewModel.SelectedIngestProfile = ingestProfile;

        Assert.False(viewModel.CanDeleteSelectedIngestProfile);
        Assert.False(viewModel.DeleteIngestProfileCommand.CanExecute(null));
        int ingestProfileCount = viewModel.IngestProfiles.Count;

        await viewModel.DeleteIngestProfileCommand.ExecuteAsync(null);

        Assert.Equal(ingestProfileCount, viewModel.IngestProfiles.Count);
        Assert.Contains(ingestProfile, viewModel.IngestProfiles);
    }

    [Fact]
    public async Task Last_custom_library_and_ingest_profiles_cannot_be_deleted()
    {
        SettingsViewModel viewModel = await CreateViewModelAsync();

        viewModel.CreateLibraryProfileCommand.Execute(null);
        LibraryProfile libraryProfile =
            Assert.IsType<LibraryProfile>(viewModel.SelectedLibraryProfile);
        foreach (LibraryProfile other in viewModel.LibraryProfiles
                     .Where(profile => !ReferenceEquals(profile, libraryProfile))
                     .ToArray())
            viewModel.LibraryProfiles.Remove(other);

        Assert.Equal(LibraryProfilePreset.Custom, libraryProfile.Preset);
        Assert.Single(viewModel.LibraryProfiles);
        Assert.False(viewModel.CanDeleteSelectedProfile);
        Assert.False(viewModel.DeleteLibraryProfileCommand.CanExecute(null));

        await viewModel.DeleteLibraryProfileCommand.ExecuteAsync(null);

        Assert.Same(libraryProfile, Assert.Single(viewModel.LibraryProfiles));

        viewModel.CreateIngestProfileCommand.Execute(null);
        LibraryIngestProfile ingestProfile =
            Assert.IsType<LibraryIngestProfile>(viewModel.SelectedIngestProfile);
        foreach (LibraryIngestProfile other in viewModel.IngestProfiles
                     .Where(profile => !ReferenceEquals(profile, ingestProfile))
                     .ToArray())
            viewModel.IngestProfiles.Remove(other);

        Assert.DoesNotContain(
            LibraryIngestProfilePresets.All,
            preset => string.Equals(
                preset.Id,
                ingestProfile.Id,
                StringComparison.OrdinalIgnoreCase));
        Assert.Single(viewModel.IngestProfiles);
        Assert.False(viewModel.CanDeleteSelectedIngestProfile);
        Assert.False(viewModel.DeleteIngestProfileCommand.CanExecute(null));

        await viewModel.DeleteIngestProfileCommand.ExecuteAsync(null);

        Assert.Same(ingestProfile, Assert.Single(viewModel.IngestProfiles));
    }

    [Fact]
    public async Task Custom_profiles_remain_deletable_when_a_fallback_exists()
    {
        SettingsViewModel viewModel = await CreateViewModelAsync();

        viewModel.CreateLibraryProfileCommand.Execute(null);
        LibraryProfile libraryProfile =
            Assert.IsType<LibraryProfile>(viewModel.SelectedLibraryProfile);

        Assert.True(viewModel.CanDeleteSelectedProfile);
        Assert.True(viewModel.DeleteLibraryProfileCommand.CanExecute(null));

        await viewModel.DeleteLibraryProfileCommand.ExecuteAsync(null);

        Assert.DoesNotContain(libraryProfile, viewModel.LibraryProfiles);

        viewModel.CreateIngestProfileCommand.Execute(null);
        LibraryIngestProfile ingestProfile =
            Assert.IsType<LibraryIngestProfile>(viewModel.SelectedIngestProfile);

        Assert.True(viewModel.CanDeleteSelectedIngestProfile);
        Assert.True(viewModel.DeleteIngestProfileCommand.CanExecute(null));

        await viewModel.DeleteIngestProfileCommand.ExecuteAsync(null);

        Assert.DoesNotContain(ingestProfile, viewModel.IngestProfiles);
    }

    private static async Task<SettingsViewModel> CreateViewModelAsync()
    {
        var viewModel = new SettingsViewModel(
            new FakeSettings(),
            new FakeFilePicker(),
            new FakeDialogs(),
            new FakeTheme());
        await viewModel.NewConfigurationCommand.ExecuteAsync(null);
        return viewModel;
    }
}

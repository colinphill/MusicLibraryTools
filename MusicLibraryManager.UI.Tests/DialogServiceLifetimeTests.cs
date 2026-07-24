using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;
using MusicLibraryManager.Services;
using MusicLibraryTools;
using Xunit;

namespace MusicLibraryManager.UI.Tests;

public sealed class DialogServiceLifetimeTests
{
    [Fact]
    public async Task Closing_fields_dialog_disposes_its_localization_subscription()
    {
        var localization =
            new TrackingLocalizationService();
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    new TestSettings());
                collection.AddSingleton<
                    IMetadataDocumentService>(
                    new TestDocumentService());
                collection.AddSingleton<
                    ILocalizationService>(
                    localization);
            });
        DialogService dialogs =
            services.GetRequiredService<
                DialogService>();

        Task<bool> pending = dialogs.ShowAsync(
            [@"C:\Music\Track.flac"]);
        FieldsRequest request =
            Assert.IsType<FieldsRequest>(
                dialogs.Current);
        FieldsDialogViewModel viewModel =
            request.ViewModel;
        LocalizedChoice<TagFields>[] choices =
            [.. viewModel.AddableFieldChoices];
        string[] labels =
            choices.Select(choice =>
                    choice.Label)
                .ToArray();

        Assert.True(
            localization.HasSubscriber(
                viewModel));

        dialogs.Complete(false);

        Assert.False(
            localization.HasSubscriber(
                viewModel));
        Assert.False(await pending);

        localization.SetCulture("fr-FR");

        Assert.Equal(
            labels,
            choices.Select(choice =>
                choice.Label));
    }

    private sealed class TrackingLocalizationService :
        ILocalizationService
    {
        private EventHandler? _cultureChanged;
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

        public event EventHandler? CultureChanged
        {
            add => _cultureChanged += value;
            remove => _cultureChanged -= value;
        }

        public bool HasSubscriber(object target) =>
            _cultureChanged?
                .GetInvocationList()
                .Any(handler =>
                    ReferenceEquals(
                        handler.Target,
                        target)) == true;

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
            _cultureChanged?.Invoke(
                this,
                EventArgs.Empty);
        }
    }

    private sealed class TestDocumentService :
        IMetadataDocumentService
    {
        public Task<MediaDocument> LoadAsync(
            string path,
            bool includeArtwork = true,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                new MediaDocument(
                    path,
                    [new(
                        "VorbisComment",
                        [new(
                            MetadataFieldKey.Known(
                                TagFields.Title),
                            ["Original title"])],
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
                    true));
        }
    }

    private sealed class TestSettings :
        IAppSettings
    {
        private readonly Dictionary<string, string>
            _preferences = [];

        public string? ConfigPath => null;
        public LibraryConfiguration? Configuration =>
            null;
        public event EventHandler?
            ConfigurationChanged;
        public AppConfigurationSnapshot GetSnapshot() =>
            new(null, null, 0);
        public void LoadConfig(string path) =>
            ConfigurationChanged?.Invoke(
                this,
                EventArgs.Empty);
        public string? GetRememberedConfigPath() =>
            null;
        public IReadOnlyList<string>
            RecentConfigPaths => [];
        public void ClearRecentConfigs()
        {
        }

        public string? GetPreference(string key) =>
            _preferences.GetValueOrDefault(
                key);

        public void SetPreference(
            string key,
            string? value)
        {
            if (value is null)
                _preferences.Remove(key);
            else
                _preferences[key] = value;
        }
    }
}

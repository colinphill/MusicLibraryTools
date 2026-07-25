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
    public async Task Destructive_confirmation_marks_the_action_independently_of_message_tone()
    {
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    new TestSettings());
                collection.AddSingleton<
                    IMetadataDocumentService>(
                    new TestDocumentService());
            });
        DialogService dialogs =
            services.GetRequiredService<
                DialogService>();

        Task<bool> pending =
            dialogs.ConfirmDestructiveAsync(
                "Discard edits?",
                "The draft will be removed.",
                "Discard");
        ConfirmRequest request =
            Assert.IsType<ConfirmRequest>(
                dialogs.Current);

        Assert.Equal(
            DialogTone.Warning,
            request.Tone);
        Assert.Equal(
            DialogActionRole.Destructive,
            request.PrimaryActionRole);

        dialogs.Complete(false);
        Assert.False(await pending);
    }

    [Fact]
    public async Task Standard_warning_confirmation_keeps_a_standard_action_role()
    {
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    new TestSettings());
                collection.AddSingleton<
                    IMetadataDocumentService>(
                    new TestDocumentService());
            });
        DialogService dialogs =
            services.GetRequiredService<
                DialogService>();

        Task<bool> pending =
            dialogs.ConfirmAsync(
                "Continue?",
                "Review the warning.",
                "Continue");
        ConfirmRequest request =
            Assert.IsType<ConfirmRequest>(
                dialogs.Current);

        Assert.Equal(
            DialogTone.Warning,
            request.Tone);
        Assert.Equal(
            DialogActionRole.Standard,
            request.PrimaryActionRole);

        dialogs.Complete(false);
        Assert.False(await pending);
    }

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

    [Fact]
    public async Task Owner_close_routes_a_dirty_fields_editor_through_its_confirmation_flow()
    {
        using ServiceProvider services =
            Composition.BuildServices(collection =>
            {
                collection.AddSingleton<IAppSettings>(
                    new TestSettings());
                collection.AddSingleton<
                    IMetadataDocumentService>(
                    new TestDocumentService());
            });
        DialogService dialogs =
            services.GetRequiredService<
                DialogService>();
        Task<bool> pending = dialogs.ShowAsync(
            [@"C:\Music\Track.flac"]);
        FieldsRequest request =
            Assert.IsType<FieldsRequest>(
                dialogs.Current);
        await request.ViewModel.Loading;
        request.ViewModel.Rows
            .Single(row =>
                row.Field == TagFields.Title)
            .Value = "Edited title";

        Assert.True(
            dialogs.HandleOwnerWindowClose());

        Assert.Same(
            request,
            dialogs.Current);
        Assert.True(
            request.ViewModel
                .IsConfirmingCancel);
        Assert.False(
            pending.IsCompleted);

        Assert.True(
            dialogs.HandleOwnerWindowClose());
        Assert.Null(
            dialogs.Current);
        Assert.False(
            await pending);
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

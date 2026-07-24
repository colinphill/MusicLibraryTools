using System.Collections;
using System.Globalization;
using System.Resources;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed class ResourceLocalizationService : ILocalizationService
{
    private const string ResourceBaseName =
        "MusicLibraryManager.Presentation.Resources.Strings";
    private const string MissingPrefix = "\u27E6";
    private const string MissingSuffix = "\u27E7";
    private static readonly string[] DefaultCultureNames =
        ["en-US"];
    private readonly IAppSettings _settings;
    private readonly ResourceManager _resources;
    private readonly CultureInfo[] _supportedCultures;
    private CultureInfo _currentUICulture;

    public ResourceLocalizationService(
        IAppSettings settings) : this(
            settings,
            DefaultCultureNames)
    {
    }

    public ResourceLocalizationService(
        IAppSettings settings,
        IReadOnlyList<string> supportedCultureNames)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(
            supportedCultureNames);
        if (supportedCultureNames.Count == 0)
            throw new ArgumentException(
                "At least one UI culture is required.",
                nameof(supportedCultureNames));

        _settings = settings;
        _resources = new ResourceManager(
            ResourceBaseName,
            typeof(ResourceLocalizationService).Assembly);
        _supportedCultures = supportedCultureNames
            .Select(CultureInfo.GetCultureInfo)
            .DistinctBy(
                culture => culture.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _currentUICulture = ResolveCulture(
            settings.GetPreference(
                LocalizationPreferences.DisplayLanguage));

        string? storedCulture = settings.GetPreference(
            LocalizationPreferences.DisplayLanguage);
        if (storedCulture is not null &&
            !string.Equals(
                storedCulture,
                _currentUICulture.Name,
                StringComparison.Ordinal))
            settings.SetPreference(
                LocalizationPreferences.DisplayLanguage,
                _currentUICulture.Name);
        CultureInfo.CurrentUICulture =
            _currentUICulture;
    }

    public CultureInfo CurrentUICulture =>
        _currentUICulture;

    public IReadOnlyList<CultureInfo>
        SupportedCultures => _supportedCultures;

    public event EventHandler? CultureChanged;

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _resources.GetString(
                   key,
                   _currentUICulture) ??
               $"{MissingPrefix}{key}{MissingSuffix}";
    }

    public string Format(
        string key,
        params object?[] arguments) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Get(key),
            arguments);

    public string FormatCount(
        string key,
        long count,
        params object?[] arguments)
    {
        string variant =
            count == 1 ? $"{key}.One" : $"{key}.Other";
        object?[] formatArguments =
            [count, .. arguments];
        return Format(variant, formatArguments);
    }

    public IReadOnlyDictionary<string, string>
        Snapshot()
    {
        ResourceSet? neutral = _resources.GetResourceSet(
            CultureInfo.InvariantCulture,
            createIfNotExists: true,
            tryParents: true);
        if (neutral is null)
            return new Dictionary<string, string>();

        var result =
            new SortedDictionary<string, string>(
                StringComparer.Ordinal);
        foreach (DictionaryEntry entry in neutral)
        {
            if (entry.Key is string key &&
                entry.Value is string)
                result[key] = Get(key);
        }
        return result;
    }

    public void SetCulture(string cultureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            cultureName);
        CultureInfo selected =
            ResolveCulture(cultureName);
        _settings.SetPreference(
            LocalizationPreferences.DisplayLanguage,
            selected.Name);
        if (string.Equals(
                selected.Name,
                _currentUICulture.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            CultureInfo.CurrentUICulture = selected;
            return;
        }

        _currentUICulture = selected;
        CultureInfo.CurrentUICulture = selected;
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private CultureInfo ResolveCulture(
        string? cultureName) =>
        _supportedCultures.FirstOrDefault(
            culture => string.Equals(
                culture.Name,
                cultureName,
                StringComparison.OrdinalIgnoreCase)) ??
        _supportedCultures[0];
}

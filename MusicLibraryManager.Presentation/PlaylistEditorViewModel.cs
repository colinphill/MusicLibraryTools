using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MusicFileUtilities;
using MusicLibrary.Core.Models;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record PlaylistGroupFieldChoice(
    string Label,
    MetadataFieldKey Field);

public sealed record PlaylistOutputRow(
    string Group,
    string File,
    int Tracks,
    int Bytes);

public partial class PlaylistEditorViewModel : ObservableObject
{
    private const string DefaultNameResourceKey =
        "Workbench.Playlists.DefaultName";
    private readonly ILocalizationService? _localization;
    private bool _nameUsesLocalizedDefault;
    private bool _settingLocalizedDefaultName;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _format = "m3u8";
    [ObservableProperty] private string? _outputPath;
    [ObservableProperty] private PlaylistPathStyle _pathStyle =
        PlaylistPathStyle.Relative;
    [ObservableProperty] private PlaylistWorkspaceEncoding _encoding =
        PlaylistWorkspaceEncoding.Utf8;
    [ObservableProperty] private PlaylistLineEnding _lineEnding =
        PlaylistLineEnding.Platform;
    [ObservableProperty] private bool _includeExtendedInfo = true;
    [ObservableProperty] private bool _onePlaylistPerGroup;
    [ObservableProperty] private PlaylistGroupFieldChoice?
        _selectedGroupField;
    [ObservableProperty] private string _groupFileNameTemplate =
        "{Name} - {Group}";

    public PlaylistEditorViewModel(
        ILocalizationService? localization = null)
    {
        _localization = localization;
        SetLocalizedDefaultName();
        RefreshLocalizedChoices();
        SelectedGroupField = GroupFields.First(choice =>
            choice.Field.KnownField == TagFields.Album);
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
    }

    public IReadOnlyList<string> Formats { get; } =
        ["m3u8", "m3u", "wpl"];
    public IReadOnlyList<PlaylistPathStyle> PathStyles { get; } =
        Enum.GetValues<PlaylistPathStyle>();
    public IReadOnlyList<PlaylistWorkspaceEncoding> Encodings { get; } =
        Enum.GetValues<PlaylistWorkspaceEncoding>();
    public IReadOnlyList<PlaylistLineEnding> LineEndings { get; } =
        Enum.GetValues<PlaylistLineEnding>();
    public ObservableCollection<PlaylistGroupFieldChoice>
        GroupFields { get; } = [];
    public ObservableCollection<LocalizedChoice<PlaylistPathStyle>>
        PathStyleChoices { get; } = [];
    public ObservableCollection<
        LocalizedChoice<PlaylistWorkspaceEncoding>>
        EncodingChoices { get; } = [];
    public ObservableCollection<LocalizedChoice<PlaylistLineEnding>>
        LineEndingChoices { get; } = [];
    public string SuggestedExtension => Format.ToLowerInvariant() switch
    {
        "m3u" => "m3u",
        "wpl" => "wpl",
        _ => "m3u8",
    };

    public event Action? Changed;

    partial void OnNameChanged(string value)
    {
        if (!_settingLocalizedDefaultName)
            _nameUsesLocalizedDefault = false;
        Changed?.Invoke();
    }
    partial void OnFormatChanged(string value)
    {
        OnPropertyChanged(nameof(SuggestedExtension));
        Changed?.Invoke();
    }
    partial void OnOutputPathChanged(string? value) => Changed?.Invoke();
    partial void OnPathStyleChanged(PlaylistPathStyle value) =>
        Changed?.Invoke();
    partial void OnEncodingChanged(PlaylistWorkspaceEncoding value) =>
        Changed?.Invoke();
    partial void OnLineEndingChanged(PlaylistLineEnding value) =>
        Changed?.Invoke();
    partial void OnIncludeExtendedInfoChanged(bool value) =>
        Changed?.Invoke();
    partial void OnOnePlaylistPerGroupChanged(bool value) =>
        Changed?.Invoke();
    partial void OnSelectedGroupFieldChanged(
        PlaylistGroupFieldChoice? value) => Changed?.Invoke();
    partial void OnGroupFileNameTemplateChanged(string value) =>
        Changed?.Invoke();

    public PlaylistWorkspaceConfiguration CreateConfiguration() =>
        new(
            Name.Trim(),
            Format.Trim(),
            OutputPath?.Trim() ?? "",
            PathStyle,
            Encoding,
            LineEnding,
            IncludeExtendedInfo,
            OnePlaylistPerGroup,
            OnePlaylistPerGroup
                ? SelectedGroupField?.Field
                : null,
            GroupFileNameTemplate);

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private void SetLocalizedDefaultName()
    {
        _settingLocalizedDefaultName = true;
        try
        {
            Name = L(DefaultNameResourceKey);
        }
        finally
        {
            _settingLocalizedDefaultName = false;
        }
        _nameUsesLocalizedDefault = true;
    }

    private void RefreshLocalizedChoices()
    {
        TagFields? selected =
            SelectedGroupField?.Field.KnownField;
        GroupFields.Clear();
        foreach (TagFields field in Enum.GetValues<TagFields>()
                     .Where(field =>
                         field != TagFields.NullField))
            GroupFields.Add(new(
                L($"Settings.Choice.TagFields.{field}"),
                MetadataFieldKey.Known(field)));
        if (selected is { } selectedField)
            SelectedGroupField = GroupFields.First(
                choice =>
                    choice.Field.KnownField == selectedField);
        RefreshChoices(
            PathStyleChoices,
            PathStyles,
            "Workbench.Choice.PlaylistPathStyle");
        RefreshChoices(
            EncodingChoices,
            Encodings,
            keyPrefix: null);
        RefreshChoices(
            LineEndingChoices,
            LineEndings,
            "Workbench.Choice.PlaylistLineEnding");
    }

    private void RefreshChoices<T>(
        ObservableCollection<LocalizedChoice<T>> target,
        IEnumerable<T> values,
        string? keyPrefix)
    {
        foreach (T value in values)
        {
            LocalizedChoice<T>? choice =
                target.FirstOrDefault(item =>
                    EqualityComparer<T>.Default.Equals(
                        item.Value,
                        value));
            string label = L(
                TechnicalLabelResourceKeys.For(value) ??
                $"{keyPrefix!}.{value}");
            if (choice is null)
                target.Add(new(value, label));
            else
                choice.Label = label;
        }
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        if (_nameUsesLocalizedDefault)
            SetLocalizedDefaultName();
        RefreshLocalizedChoices();
    }
}

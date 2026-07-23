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
    [ObservableProperty] private string _name = "Music playlist";
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

    public PlaylistEditorViewModel()
    {
        GroupFields = Enum.GetValues<TagFields>()
            .Where(field => field != TagFields.NullField)
            .Select(field => new PlaylistGroupFieldChoice(
                field.ToString(),
                MetadataFieldKey.Known(field)))
            .ToArray();
        SelectedGroupField = GroupFields.First(choice =>
            choice.Field.KnownField == TagFields.Album);
    }

    public IReadOnlyList<string> Formats { get; } =
        ["m3u8", "m3u", "wpl"];
    public IReadOnlyList<PlaylistPathStyle> PathStyles { get; } =
        Enum.GetValues<PlaylistPathStyle>();
    public IReadOnlyList<PlaylistWorkspaceEncoding> Encodings { get; } =
        Enum.GetValues<PlaylistWorkspaceEncoding>();
    public IReadOnlyList<PlaylistLineEnding> LineEndings { get; } =
        Enum.GetValues<PlaylistLineEnding>();
    public IReadOnlyList<PlaylistGroupFieldChoice> GroupFields { get; }
    public string SuggestedExtension => Format.ToLowerInvariant() switch
    {
        "m3u" => "m3u",
        "wpl" => "wpl",
        _ => "m3u8",
    };

    public event Action? Changed;

    partial void OnNameChanged(string value) => Changed?.Invoke();
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
}

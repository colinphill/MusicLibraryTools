using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicLibrary.Core.Services;

namespace MusicLibraryManager.Presentation;

public sealed record ExternalToolInvocationRow(
    int Number,
    string Executable,
    string Arguments,
    string WorkingDirectory,
    int Files);

public partial class ExternalToolEditorViewModel : ObservableObject
{
    private readonly IExternalToolStore? _store;
    private bool _loading;

    [ObservableProperty] private ExternalToolDefinition?
        _selectedSavedTool;
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _name = "External tool";
    [ObservableProperty] private string? _executable;
    [ObservableProperty] private string _argumentsText = "{Files}";
    [ObservableProperty] private string? _workingDirectory;
    [ObservableProperty] private ExternalToolInvocationMode
        _invocationMode =
            ExternalToolInvocationMode.OnceForSelection;
    [ObservableProperty] private string _storeStatus = "";

    public ExternalToolEditorViewModel(
        IExternalToolStore? store = null)
    {
        _store = store;
        ReloadSavedTools();
    }

    public ObservableCollection<ExternalToolDefinition>
        SavedTools { get; } = [];
    public IReadOnlyList<ExternalToolInvocationMode>
        InvocationModes { get; } =
            Enum.GetValues<ExternalToolInvocationMode>();

    public event Action? Changed;

    partial void OnIdChanged(Guid value) => RaiseChanged();
    partial void OnNameChanged(string value) => RaiseChanged();
    partial void OnExecutableChanged(string? value) => RaiseChanged();
    partial void OnArgumentsTextChanged(string value) => RaiseChanged();
    partial void OnWorkingDirectoryChanged(string? value) =>
        RaiseChanged();
    partial void OnInvocationModeChanged(
        ExternalToolInvocationMode value) => RaiseChanged();

    public ExternalToolDefinition CreateDefinition() =>
        new(
            Id == Guid.Empty ? Guid.NewGuid() : Id,
            Name.Trim(),
            Executable?.Trim() ?? "",
            ParseArguments(ArgumentsText),
            string.IsNullOrWhiteSpace(WorkingDirectory)
                ? null
                : WorkingDirectory.Trim(),
            InvocationMode);

    [RelayCommand]
    private void NewTool()
    {
        _loading = true;
        try
        {
            SelectedSavedTool = null;
            Id = Guid.NewGuid();
            Name = "External tool";
            Executable = null;
            ArgumentsText = "{Files}";
            WorkingDirectory = null;
            InvocationMode =
                ExternalToolInvocationMode.OnceForSelection;
            StoreStatus = "New unsaved tool.";
        }
        finally
        {
            _loading = false;
        }
        Changed?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanLoadTool))]
    private void LoadTool()
    {
        if (SelectedSavedTool is null)
            return;
        Load(SelectedSavedTool);
        StoreStatus = $"Loaded '{SelectedSavedTool.Name}'.";
    }

    private bool CanLoadTool() => SelectedSavedTool is not null;

    [RelayCommand(CanExecute = nameof(CanSaveTool))]
    private void SaveTool()
    {
        if (_store is null)
            return;
        ExternalToolDefinition definition = CreateDefinition();
        _store.Save(definition);
        Id = definition.Id;
        ReloadSavedTools();
        SelectedSavedTool = SavedTools.FirstOrDefault(tool =>
            tool.Id == definition.Id);
        StoreStatus = $"Saved '{definition.Name}' as a personal tool.";
    }

    private bool CanSaveTool() =>
        _store is not null &&
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Executable);

    [RelayCommand(CanExecute = nameof(CanDeleteTool))]
    private void DeleteTool()
    {
        if (_store is null || SelectedSavedTool is null)
            return;
        Guid id = SelectedSavedTool.Id;
        string name = SelectedSavedTool.Name;
        _store.Delete(id);
        ReloadSavedTools();
        NewTool();
        StoreStatus = $"Deleted personal tool '{name}'.";
    }

    private bool CanDeleteTool() =>
        _store is not null && SelectedSavedTool is not null;

    partial void OnSelectedSavedToolChanged(
        ExternalToolDefinition? value)
    {
        LoadToolCommand.NotifyCanExecuteChanged();
        DeleteToolCommand.NotifyCanExecuteChanged();
    }

    private void Load(ExternalToolDefinition definition)
    {
        _loading = true;
        try
        {
            Id = definition.Id;
            Name = definition.Name;
            Executable = definition.Executable;
            ArgumentsText = string.Join(
                Environment.NewLine,
                definition.Arguments.IsDefault
                    ? []
                    : definition.Arguments);
            WorkingDirectory = definition.WorkingDirectory;
            InvocationMode = definition.InvocationMode;
        }
        finally
        {
            _loading = false;
        }
        Changed?.Invoke();
    }

    private void ReloadSavedTools()
    {
        SavedTools.Clear();
        IEnumerable<ExternalToolDefinition> tools =
            _store is null
                ? []
                : _store.Load().OrderBy(
                    tool => tool.Name,
                    StringComparer.OrdinalIgnoreCase);
        foreach (ExternalToolDefinition tool in tools)
            SavedTools.Add(tool);
        LoadToolCommand.NotifyCanExecuteChanged();
        DeleteToolCommand.NotifyCanExecuteChanged();
    }

    private void RaiseChanged()
    {
        SaveToolCommand.NotifyCanExecuteChanged();
        if (!_loading)
            Changed?.Invoke();
    }

    private static ImmutableArray<string> ParseArguments(
        string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(argument => argument.Trim())
            .Where(argument => argument.Length > 0)
            .ToImmutableArray();
}

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
    private readonly ILocalizationService? _localization;
    private bool _loading;
    private string? _storeStatusKey;
    private object?[] _storeStatusArguments = [];

    [ObservableProperty] private ExternalToolDefinition?
        _selectedSavedTool;
    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _executable;
    [ObservableProperty] private string _argumentsText = "{Files}";
    [ObservableProperty] private string? _workingDirectory;
    [ObservableProperty] private ExternalToolInvocationMode
        _invocationMode =
            ExternalToolInvocationMode.OnceForSelection;
    [ObservableProperty] private string _storeStatus = "";

    public ExternalToolEditorViewModel(
        IExternalToolStore? store = null,
        ILocalizationService? localization = null)
    {
        _store = store;
        _localization = localization;
        Name = L("Workbench.Tools.DefaultName");
        RefreshLocalizedChoices();
        if (_localization is not null)
            _localization.CultureChanged +=
                OnLocalizationCultureChanged;
        ReloadSavedTools();
    }

    public ObservableCollection<ExternalToolDefinition>
        SavedTools { get; } = [];
    public IReadOnlyList<ExternalToolInvocationMode>
        InvocationModes { get; } =
            Enum.GetValues<ExternalToolInvocationMode>();
    public ObservableCollection<
        LocalizedChoice<ExternalToolInvocationMode>>
        InvocationModeChoices { get; } = [];

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
            Name = L("Workbench.Tools.DefaultName");
            Executable = null;
            ArgumentsText = "{Files}";
            WorkingDirectory = null;
            InvocationMode =
                ExternalToolInvocationMode.OnceForSelection;
            SetStatus("Workbench.Tools.Status.New");
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
        SetStatus(
            "Workbench.Tools.Status.Loaded",
            SelectedSavedTool.Name);
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
        SetStatus(
            "Workbench.Tools.Status.Saved",
            definition.Name);
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
        SetStatus(
            "Workbench.Tools.Status.Deleted",
            name);
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

    private string L(string key) =>
        _localization?.Get(key) ??
        LocalizedText.Get(key);

    private string LF(
        string key,
        params object?[] arguments) =>
        _localization?.Format(key, arguments) ??
        LocalizedText.Format(key, arguments);

    private void SetStatus(
        string key,
        params object?[] arguments)
    {
        _storeStatusKey = key;
        _storeStatusArguments = arguments;
        StoreStatus = LF(key, arguments);
    }

    private void RefreshLocalizedChoices()
    {
        foreach (ExternalToolInvocationMode value in
                 InvocationModes)
        {
            LocalizedChoice<ExternalToolInvocationMode>?
                choice = InvocationModeChoices.FirstOrDefault(
                    item => item.Value == value);
            string label = L(
                $"Workbench.Choice.ExternalToolInvocationMode.{value}");
            if (choice is null)
                InvocationModeChoices.Add(new(value, label));
            else
                choice.Label = label;
        }
    }

    private void OnLocalizationCultureChanged(
        object? sender,
        EventArgs e)
    {
        RefreshLocalizedChoices();
        if (_storeStatusKey is not null)
            StoreStatus = LF(
                _storeStatusKey,
                _storeStatusArguments);
    }
}

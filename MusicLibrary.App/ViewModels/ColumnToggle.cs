using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicLibrary.App.ViewModels;

/// <summary>A checkable column in the details-grid column chooser.</summary>
public partial class ColumnToggle : ObservableObject
{
    public string Key { get; }
    public string Header { get; }

    [ObservableProperty]
    private bool _isSelected;

    public event Action? Changed;

    public ColumnToggle(string key, string header, bool selected)
    {
        Key = key;
        Header = header;
        _isSelected = selected;
    }

    partial void OnIsSelectedChanged(bool value) => Changed?.Invoke();
}

/// <summary>A filter-scope choice: a specific column (Key) or all visible columns (Key = null).</summary>
public sealed record FilterScopeOption(string? Key, string Label)
{
    public override string ToString() => Label;
}

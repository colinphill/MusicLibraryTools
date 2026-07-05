using CommunityToolkit.Mvvm.ComponentModel;
using MusicFileUtilities;

namespace MusicLibrary.App.ViewModels;

/// <summary>
/// One editable tag field in the editor. Tracks whether the value differs across a multi-file
/// selection ("mixed") and whether the user has changed it since the targets were loaded.
/// </summary>
public partial class EditableField : ObservableObject
{
    public TagFields Field { get; }
    public string Label { get; }

    private string _originalValue = "";
    private bool _suppressDirty;

    [ObservableProperty]
    private string _value = "";

    /// <summary>True when the selected files hold different values for this field.</summary>
    [ObservableProperty]
    private bool _isMixed;

    /// <summary>True once the user edits the value; only modified fields are written.</summary>
    [ObservableProperty]
    private bool _isModified;

    public EditableField(TagFields field, string label)
    {
        Field = field;
        Label = label;
    }

    /// <summary>Set the loaded value(s). <paramref name="mixed"/> marks differing values across files.</summary>
    public void SetLoaded(string value, bool mixed)
    {
        _suppressDirty = true;
        Value = value;
        _suppressDirty = false;
        _originalValue = value;
        IsMixed = mixed;
        IsModified = false;
    }

    partial void OnValueChanged(string value)
    {
        if (_suppressDirty)
            return;
        IsModified = value != _originalValue;
        if (IsModified)
            IsMixed = false;
    }
}

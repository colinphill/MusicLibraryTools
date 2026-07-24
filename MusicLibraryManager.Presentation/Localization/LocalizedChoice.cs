using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicLibraryManager.Presentation;

public interface ILocalizedChoice
{
    object? UntypedValue { get; }
}

public sealed class LocalizedChoice<T>(
    T value,
    string label) : ObservableObject, ILocalizedChoice
{
    private string _label = label;

    public T Value { get; } = value;
    object? ILocalizedChoice.UntypedValue => Value;

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public override string ToString() => Label;
}

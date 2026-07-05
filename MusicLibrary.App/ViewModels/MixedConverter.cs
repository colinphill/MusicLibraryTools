using System.Globalization;
using Avalonia.Data.Converters;

namespace MusicLibrary.App.ViewModels;

/// <summary>Renders a "(mixed values)" placeholder when a batch field holds differing values.</summary>
public sealed class MixedConverter : IValueConverter
{
    public static readonly MixedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "(mixed values — leave blank to keep)" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

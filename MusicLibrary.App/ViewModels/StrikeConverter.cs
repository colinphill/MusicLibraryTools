using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MusicLibrary.App.ViewModels;

/// <summary>bool → strikethrough TextDecorations (true) or none, for "will be removed" rows.</summary>
public sealed class StrikeConverter : IValueConverter
{
    public static readonly StrikeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TextDecorations.Strikethrough : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

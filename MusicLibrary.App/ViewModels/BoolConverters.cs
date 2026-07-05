using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MusicLibrary.App.ViewModels;

/// <summary>Italicizes a value when it represents a mixed/aggregated ("multiple values") field.</summary>
public sealed class MixedFontStyleConverter : IValueConverter
{
    public static readonly MixedFontStyleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontStyle.Italic : FontStyle.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows just the file name of a full path (for the collapsed recent-configs box).</summary>
public sealed class PathToFileNameConverter : IValueConverter
{
    public static readonly PathToFileNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string path && path.Length > 0 ? System.IO.Path.GetFileName(path) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

using Avalonia.Controls;
using Avalonia.Data.Converters;
using System.Globalization;
using MusicFileUtilities;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchInspectorDrawerView :
    UserControl
{
    public WorkbenchInspectorDrawerView()
    {
        InitializeComponent();
        WorkbenchInspectorView.CloseRequested +=
            (_, _) =>
                CloseRequested?.Invoke(
                    this,
                    EventArgs.Empty);
        WorkbenchInspectorView.ReviewChangesRequested +=
            (_, _) =>
                ReviewChangesRequested?.Invoke(
                    this,
                    EventArgs.Empty);
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ReviewChangesRequested;

    public Control InitialFocus =>
        WorkbenchInspectorView.CloseButton;
}

public sealed class TagLayerPresenceConverter :
    IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        IsPresent(
            value,
            parameter);

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();

    internal static bool IsPresent(
        object? value,
        object? parameter) =>
        Enum.TryParse(
            parameter?.ToString(),
            ignoreCase: false,
            out TagLayerKind kind) &&
        value is IEnumerable<TagLayerDescriptor> layers &&
        layers.Any(layer =>
            layer.Kind == kind &&
            layer.IsPresent);
}

public sealed class TagLayerMissingConverter :
    IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        !TagLayerPresenceConverter.IsPresent(
            value,
            parameter);

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Globalization;
using MusicLibrary.Core.Services;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views.WorkbenchSections;

public partial class WorkbenchShortcutsSectionView : UserControl
{
    private bool _narrow;
    private bool _showNarrowEditor;

    public WorkbenchShortcutsSectionView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        bool narrow = Bounds.Width > 0 &&
            Bounds.Width < 880;
        _narrow = narrow;
        bool compactHeight = Bounds.Height > 0 &&
            Bounds.Height < 430;
        GestureHelp.IsVisible = !compactHeight;
        SafetyNote.IsVisible = !compactHeight;
        InputWarning.IsVisible = !compactHeight;
        SectionLayout.ColumnDefinitions.Clear();
        SectionLayout.RowDefinitions.Clear();
        if (narrow)
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(1, GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(EditorScroll, 0);
            Grid.SetRow(EditorScroll, 0);
        }
        else
        {
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        1.1,
                        GridUnitType.Star)));
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(new GridLength(14)));
            SectionLayout.ColumnDefinitions.Add(
                new ColumnDefinition(
                    new GridLength(
                        0.9,
                        GridUnitType.Star)));
            SectionLayout.RowDefinitions.Add(
                new RowDefinition(
                    new GridLength(
                        1,
                        GridUnitType.Star)));
            Grid.SetColumn(EditorScroll, 2);
            Grid.SetRow(EditorScroll, 0);
        }
        ApplyPaneVisibility();
    }

    private void ApplyPaneVisibility()
    {
        BindingsPanel.IsVisible =
            !_narrow ||
            !_showNarrowEditor;
        EditorScroll.IsVisible =
            !_narrow ||
            _showNarrowEditor;
        ShortcutBackButton.IsVisible =
            _narrow &&
            _showNarrowEditor;
    }

    private void OnShortcutListPointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        if (!_narrow)
            return;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ShortcutBindingList.SelectedItem is not null)
                    ShowNarrowEditor();
            });
    }

    private void OnShortcutListKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (!_narrow ||
            e.Key is not (Key.Enter or Key.Right) ||
            ShortcutBindingList.SelectedItem is null)
            return;
        ShowNarrowEditor();
        e.Handled = true;
    }

    private void OnNewShortcutClick(
        object? sender,
        RoutedEventArgs e)
    {
        if (_narrow)
            ShowNarrowEditor();
    }

    private void OnShortcutBackClick(
        object? sender,
        RoutedEventArgs e)
    {
        _showNarrowEditor = false;
        ApplyPaneVisibility();
        ShortcutBindingList.Focus();
    }

    private void ShowNarrowEditor()
    {
        _showNarrowEditor = true;
        ApplyPaneVisibility();
        Dispatcher.UIThread.Post(
            () => GestureTextBox.Focus());
    }
}

public sealed class ShortcutTargetKindMatchConverter :
    IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        value is WorkbenchShortcutTargetKind kind &&
        Enum.TryParse(
            parameter?.ToString(),
            ignoreCase: false,
            out WorkbenchShortcutTargetKind expected) &&
        kind == expected;

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class FieldsEditorView : UserControl
{
    public FieldsEditorView()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
            ApplyResponsiveLayout();
    }

    public FieldsEditorView(FieldsDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void ApplyResponsiveLayout()
    {
        bool stacked =
            Bounds.Width > 0 &&
            Bounds.Width < 760;
        FieldAdditionChoices.Columns =
            stacked ? 1 : 2;
        if (FieldAdditionChoices.Children.Count < 2)
            return;
        FieldAdditionChoices.Children[0].Margin =
            stacked
                ? new global::Avalonia.Thickness(
                    0,
                    0,
                    0,
                    4)
                : new global::Avalonia.Thickness(
                    0,
                    0,
                    4,
                    0);
        FieldAdditionChoices.Children[1].Margin =
            stacked
                ? new global::Avalonia.Thickness(
                    0,
                    4,
                    0,
                    0)
                : new global::Avalonia.Thickness(
                    4,
                    0,
                    0,
                    0);
    }
}

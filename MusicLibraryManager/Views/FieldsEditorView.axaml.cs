using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibraryManager.Presentation;

namespace MusicLibraryManager.Views;

public partial class FieldsEditorView : UserControl
{
    public FieldsEditorView() => InitializeComponent();

    public FieldsEditorView(FieldsDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}

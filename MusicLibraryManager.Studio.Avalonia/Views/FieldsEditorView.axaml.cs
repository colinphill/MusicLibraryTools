using global::Avalonia.Controls;
using global::Avalonia.Markup.Xaml;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Studio.Avalonia.Views;

public partial class FieldsEditorView : UserControl
{
    public FieldsEditorView() => InitializeComponent();

    public FieldsEditorView(FieldsDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}

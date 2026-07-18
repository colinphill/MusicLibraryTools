using System.Windows;
using System.Windows.Controls;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Pages;

public partial class IngestPage : UserControl
{
    private readonly IngestViewModel _viewModel;

    public IngestPage()
    {
        InitializeComponent();
        _viewModel = App.GetService<IngestViewModel>();
        DataContext = _viewModel;
    }

    private void IngestPage_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void IngestPage_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            _viewModel.SetDroppedSource(paths[0]);
        e.Handled = true;
    }
}

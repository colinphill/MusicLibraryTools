using System.ComponentModel;
using System.Windows;
using MusicLibrary.App.ViewModels;

namespace MusicLibraryManager.Dialogs;

public partial class FieldsDialog : Window
{
    private readonly FieldsDialogViewModel _viewModel;

    public FieldsDialog(FieldsDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += CloseRequested;
        Closing += FieldsDialog_Closing;
    }

    private void CloseRequested(bool saved)
        => DialogResult = saved;

    private void FieldsDialog_Closing(object? sender, CancelEventArgs e)
        => _viewModel.CloseRequested -= CloseRequested;
}
